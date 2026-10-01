using System.Collections.ObjectModel;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DogeDebugger.Core.Modules;
using DogeDebugger.Core.Search;
using DogeDebugger.Debugger.Session;
using DogeDebugger.Plugins.CEMono.Protocol;
using Microsoft.Win32;

namespace DogeDebugger.Plugins.CEMono.UI;

public partial class MonoExplorerViewModel : ObservableObject, IDisposable
{
    private const int MaximumMonoStringLength = 4096;
    private const int MaximumInstanceSearchResults = 250_000;

    private static readonly string[] MonoRuntimeModuleNames =
    [
        "mono.dll",
        "mono-2.0-bdwgc.dll",
        "libmono",
        "UnityPlayer.dll"
    ];

    private static readonly string[] Il2CppRuntimeModuleNames =
    [
        "libil2cpp",
        "GameAssembly.dll"
    ];

    private readonly DebuggerSession _session;
    private readonly Action<ulong>? _navigateToAddress;
    private readonly Action<string, string>? _showIlDisassembly;
    private readonly Action<ulong, string>? _registerSymbol;
    private MonoDataCollectorClient? _client;
    private CancellationTokenSource? _runtimeDetectionCancellation;
    private CancellationTokenSource? _instanceLookupCancellation;
    private int _runtimeDetectionVersion;
    private bool _suppressSelectionLoading;
    private bool _suppressInstanceAddressApply;
    private bool _disposed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InjectMonoCommand))]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isIl2Cpp;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InjectMonoCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshTreeCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyAssemblySymbolsCommand))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private bool _hasSupportedRuntime;

    [ObservableProperty]
    private string _statusText = "未打开进程";

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _classSearchText = string.Empty;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private int _processId;

    [ObservableProperty]
    private MonoImageItem? _selectedImage;

    [ObservableProperty]
    private MonoClassItem? _selectedClass;

    [ObservableProperty]
    private string _classInheritanceChain = string.Empty;

    [ObservableProperty]
    private string _instanceAddress = string.Empty;

    [ObservableProperty]
    private string? _selectedInstanceAddress;

    public MonoExplorerViewModel(
        DebuggerSession session,
        Action<ulong>? navigateToAddress = null,
        Action<string, string>? showIlDisassembly = null,
        Action<ulong, string>? registerSymbol = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _navigateToAddress = navigateToAddress;
        _showIlDisassembly = showIlDisassembly;
        _registerSymbol = registerSymbol;
    }

    public ObservableCollection<MonoImageItem> Images { get; } = [];

    public ObservableCollection<MonoClassItem> FilteredClasses { get; } = [];

    public ObservableCollection<MonoStaticFieldRow> StaticFields { get; } = [];

    public ObservableCollection<MonoFieldRow> Fields { get; } = [];

    public ObservableCollection<MonoMethodRow> Methods { get; } = [];

    public ObservableCollection<string> InstanceAddresses { get; } = [];

    public ObservableCollection<MonoInheritanceItem> Inheritance { get; } = [];

    public void OnTargetChanged()
    {
        CancelRuntimeDetection();
        CancelInstanceLookup();
        DisposeClient();
        ClearBrowserState();

        if (!_session.Target.IsOpen)
        {
            ResetProcessState("未打开进程");
            return;
        }

        ProcessId = _session.Target.ProcessId;
        _ = DetectRuntimeForProcessAsync(ProcessId);
    }

    public void NavigateToInstanceAddress(
        ulong address,
        string? registerName)
    {
        if (address == 0)
        {
            return;
        }

        InstanceAddress = $"0x{address:X}";
        if (IsConnected)
        {
            ApplyInstanceAddress(address);
        }

        StatusText = string.IsNullOrWhiteSpace(registerName)
            ? $"已定位到 Mono 实例 0x{address:X}。"
            : $"已定位到 {registerName} 指向的 Mono 实例 0x{address:X}。";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelRuntimeDetection();
        CancelInstanceLookup();
        DisposeClient();
        GC.SuppressFinalize(this);
    }

    [RelayCommand(CanExecute = nameof(CanInjectMono))]
    private async Task InjectMonoAsync()
    {
        if (!_session.Target.IsOpen)
        {
            StatusText = "请先打开一个进程";
            return;
        }

        string pluginPath = Path.Combine(
            AppContext.BaseDirectory,
            "Plugins",
            "unity64.dll");
        if (!File.Exists(pluginPath))
        {
            StatusText = $"Unity插件注入失败: 未找到 {pluginPath}";
            return;
        }

        IsLoading = true;
        StatusText = "正在注入Unity插件...";
        try
        {
            ulong module = await Task.Run(
                    () => _session.InjectDll(pluginPath),
                    CancellationToken.None)
                .ConfigureAwait(true);
            if (module == 0)
            {
                StatusText = "Unity插件注入失败: LoadLibraryW 返回 0。";
                return;
            }

            await Task.Delay(150).ConfigureAwait(true);
            await DetectRuntimeForProcessAsync(
                    _session.Target.ProcessId,
                    forceRefresh: true)
                .ConfigureAwait(true);
            StatusText = HasSupportedRuntime
                ? "Unity插件已注入，可点击连接继续浏览。"
                : "Unity插件已注入，但未检测到 Mono/IL2CPP 运行时。";
        }
        catch (Exception exception)
        {
            StatusText = "Unity插件注入失败: " + exception.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanInjectMono() =>
        !_disposed && _session.Target.IsOpen && !IsLoading;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (!HasSupportedRuntime || ProcessId == 0)
        {
            StatusText = "未检测到 Mono/IL2CPP 运行时";
            return;
        }

        IsLoading = true;
        StatusText = "正在连接...";
        MonoDataCollectorClient client = new(ProcessId);
        try
        {
            await client.ConnectAsync().ConfigureAwait(true);
            if (!client.IsConnected)
            {
                client.Dispose();
                StatusText = "连接失败";
                return;
            }

            DisposeClient();
            _client = client;
            IsConnected = true;
            IsIl2Cpp = client.IsIl2Cpp;
            StatusText = IsIl2Cpp ? "已连接 (IL2CPP)" : "已连接 (Mono)";
            await LoadImagesAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            client.Dispose();
            StatusText = "连接错误: " + exception.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanConnect() =>
        !_disposed &&
        !IsConnected &&
        !IsLoading &&
        HasSupportedRuntime &&
        ProcessId != 0;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private void Disconnect()
    {
        DisposeClient();
        IsConnected = false;
        IsIl2Cpp = false;
        ClearBrowserState();
        StatusText = "已断开";
    }

    private bool CanDisconnect() =>
        !_disposed && IsConnected && !IsLoading;

    [RelayCommand(CanExecute = nameof(CanRefreshTree))]
    private async Task RefreshTreeAsync()
    {
        try
        {
            await LoadImagesAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            StatusText = "刷新错误: " + exception.Message;
        }
    }

    private bool CanRefreshTree() =>
        !_disposed && IsConnected && !IsLoading;

    [RelayCommand(CanExecute = nameof(CanExportSelectedImage))]
    private async Task ExportSelectedImageAsync()
    {
        MonoImageItem? image = SelectedImage;
        MonoDataCollectorClient? client = _client;
        if (image is null || client is null)
        {
            return;
        }

        SaveFileDialog dialog = new()
        {
            Title = "导出 Mono/IL2CPP 程序集",
            Filter = "C# 源文件 (*.cs)|*.cs|所有文件 (*.*)|*.*",
            FileName = GetSafeFileName(image.Name) + ".cs"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsLoading = true;
        StatusText = $"正在导出 {image.Name}...";
        try
        {
            MonoImageDump? dump = await Task.Run(
                    () => client.DumpImage(image.Name),
                    CancellationToken.None)
                .ConfigureAwait(true);
            if (dump is null || dump.Data.Length == 0)
            {
                StatusText = "导出失败: DataCollector 未返回程序集内容。";
                return;
            }

            await File.WriteAllBytesAsync(dialog.FileName, dump.Data)
                .ConfigureAwait(true);
            StatusText = $"已导出程序集: {dialog.FileName}";
        }
        catch (Exception exception)
        {
            StatusText = "导出错误: " + exception.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanExportSelectedImage() =>
        !_disposed && IsConnected && !IsLoading && SelectedImage is not null;

    [RelayCommand(CanExecute = nameof(CanApplyAssemblySymbols))]
    private async Task ApplyAssemblySymbolsAsync()
    {
        MonoDataCollectorClient? client = _client;
        if (client is null || Images.Count == 0)
        {
            return;
        }

        IsLoading = true;
        Progress = 0;
        int symbolCount = 0;
        try
        {
            List<MonoImageItem> images = Images.ToList();
            for (int imageIndex = 0; imageIndex < images.Count; imageIndex++)
            {
                MonoImageItem image = images[imageIndex];
                StatusText = $"正在读取 {image.Name} 的方法符号...";
                IReadOnlyList<MonoClassInfo> classes = await Task.Run(
                        () => client.GetClasses(image.Handle),
                        CancellationToken.None)
                    .ConfigureAwait(true);

                foreach (MonoClassInfo classInfo in classes)
                {
                    IReadOnlyList<MonoMethodInfo> methods = await Task.Run(
                            () => client.GetMethods(classInfo.Handle),
                            CancellationToken.None)
                        .ConfigureAwait(true);
                    string className = string.IsNullOrWhiteSpace(classInfo.FullName)
                        ? classInfo.Name
                        : classInfo.FullName;
                    foreach (MonoMethodInfo method in methods)
                    {
                        ulong address = await Task.Run(
                                () => client.CompileMethod(method.Handle),
                                CancellationToken.None)
                            .ConfigureAwait(true);
                        if (address == 0)
                        {
                            continue;
                        }

                        _registerSymbol?.Invoke(
                            address,
                            $"{className}.{method.Name}");
                        symbolCount++;
                    }
                }

                Progress = (imageIndex + 1d) / images.Count;
            }

            StatusText = $"已应用 {symbolCount:N0} 个 Mono/IL2CPP 方法符号";
        }
        catch (Exception exception)
        {
            StatusText = "应用符号错误: " + exception.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanApplyAssemblySymbols() =>
        !_disposed && IsConnected && !IsLoading && Images.Count > 0;

    [RelayCommand]
    private async Task JitCompileMethodAsync(MonoMethodRow? method)
    {
        MonoDataCollectorClient? client = _client;
        if (method is null || client is null)
        {
            return;
        }

        try
        {
            ulong address = await Task.Run(
                    () => client.CompileMethod(method.Handle),
                    CancellationToken.None)
                .ConfigureAwait(true);
            if (address == 0)
            {
                StatusText = $"无法编译方法 {method.Name}";
                return;
            }

            method.NativeAddress = $"0x{address:X}";
            string symbolName = string.IsNullOrWhiteSpace(method.ClassName)
                ? method.Name
                : $"{method.ClassName}.{method.Name}";
            _registerSymbol?.Invoke(address, symbolName);
            _navigateToAddress?.Invoke(address);
        }
        catch (Exception exception)
        {
            StatusText = "JIT 编译错误: " + exception.Message;
        }
    }

    [RelayCommand]
    private async Task ShowIlDisassemblyAsync(MonoMethodRow? method)
    {
        MonoDataCollectorClient? client = _client;
        if (method is null || client is null)
        {
            return;
        }

        try
        {
            string? il = await Task.Run(
                    () => client.DisassembleMethod(method.Handle),
                    CancellationToken.None)
                .ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(il))
            {
                StatusText = "当前目标未返回 IL 反汇编。";
                return;
            }

            _showIlDisassembly?.Invoke(method.Name, il);
        }
        catch (Exception exception)
        {
            StatusText = "IL 反汇编错误: " + exception.Message;
        }
    }

    [RelayCommand]
    private async Task ToggleFieldExpandAsync(MonoFieldRow? row)
    {
        MonoDataCollectorClient? client = _client;
        if (row is null || client is null || !row.IsExpandable)
        {
            return;
        }

        if (row.IsExpanded)
        {
            CollapseField(row);
            return;
        }

        int insertIndex = Fields.IndexOf(row);
        if (insertIndex < 0)
        {
            return;
        }

        try
        {
            List<MonoFieldRow> children = await Task.Run(
                    () => BuildChildFields(client, row),
                    CancellationToken.None)
                .ConfigureAwait(true);
            if (children.Count == 0)
            {
                row.IsExpanded = true;
                return;
            }

            for (int index = 0; index < children.Count; index++)
            {
                Fields.Insert(insertIndex + 1 + index, children[index]);
            }

            row.LoadedChildCount = children.Count;
            row.IsExpanded = true;
            ApplyInstanceAddressIfAvailable();
        }
        catch (Exception exception)
        {
            StatusText = "字段展开错误: " + exception.Message;
        }
    }

    [RelayCommand]
    private async Task LookupInstancesAsync()
    {
        MonoClassItem? selectedClass = SelectedClass;
        MonoDataCollectorClient? client = _client;
        if (selectedClass is null ||
            client is null ||
            !_session.Target.IsOpen ||
            !IsConnected)
        {
            StatusText = "Select a connected Mono class first.";
            return;
        }

        CancelInstanceLookup();
        CancellationTokenSource cancellation = new();
        _instanceLookupCancellation = cancellation;
        InstanceAddresses.Clear();
        ClearInstanceSelection();
        IsLoading = true;
        StatusText = "Searching class instances... 0%";
        try
        {
            byte[] patternBytes = new byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64LittleEndian(
                patternBytes,
                selectedClass.Handle);
            var pattern = BytePattern.FromMask(
                patternBytes,
                Enumerable.Repeat((byte)1, patternBytes.Length).ToArray());

            SearchResult result = await Task.Run(
                    () => _session.Search(
                        new SearchRequest
                        {
                            Pattern = pattern.DisplayText,
                            MaximumResults = MaximumInstanceSearchResults,
                            SearchPrivateMemory = true,
                            SearchImageMemory = true,
                            SearchMappedMemory = false
                        },
                        cancellation.Token),
                    cancellation.Token)
                .ConfigureAwait(true);

            List<ulong> candidates = [];
            for (int index = 0; index < result.Matches.Count; index++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                ulong candidate = result.Matches[index].Address;
                ulong classHandle = await Task.Run(
                        () => client.GetObjectClass(candidate),
                        cancellation.Token)
                    .ConfigureAwait(true);
                if (classHandle == selectedClass.Handle)
                {
                    candidates.Add(candidate);
                }

                if ((index & 0x3FF) == 0)
                {
                    Progress = (index + 1d) / Math.Max(1, result.Matches.Count);
                    StatusText =
                        $"Searching class instances... {Progress:P0}";
                }
            }

            foreach (ulong address in candidates.Distinct().Order())
            {
                InstanceAddresses.Add($"0x{address:X}");
            }

            if (InstanceAddresses.Count > 0)
            {
                _suppressInstanceAddressApply = true;
                try
                {
                    SelectedInstanceAddress = InstanceAddresses[0];
                    InstanceAddress = InstanceAddresses[0];
                }
                finally
                {
                    _suppressInstanceAddressApply = false;
                }

                ApplyInstanceAddress(candidates.Distinct().Order().First());
            }

            StatusText =
                $"Found {InstanceAddresses.Count:N0} instance(s).";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_instanceLookupCancellation, cancellation))
            {
                StatusText = "Instance search failed: " + exception.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_instanceLookupCancellation, cancellation))
            {
                _instanceLookupCancellation = null;
                IsLoading = false;
            }

            cancellation.Dispose();
        }
    }

    [RelayCommand]
    private void ResolveInstanceAddress()
    {
        if (!TryResolveAddress(InstanceAddress, out ulong address) ||
            address == 0)
        {
            ClearFieldValues();
            StatusText = "Invalid instance address.";
            return;
        }

        ApplyInstanceAddress(address);
        StatusText = $"Instance address resolved: 0x{address:X}";
    }

    [RelayCommand]
    private void CopyAddress()
    {
        if (SelectedClass is not null)
        {
            Clipboard.SetText(SelectedClass.Handle.ToString("X"));
        }
    }

    [RelayCommand]
    private void CopyName()
    {
        if (SelectedClass is not null)
        {
            Clipboard.SetText(SelectedClass.DisplayName);
        }
    }

    partial void OnSelectedImageChanged(MonoImageItem? value)
    {
        ExportSelectedImageCommand.NotifyCanExecuteChanged();
        if (_suppressSelectionLoading || value is null || _client is null)
        {
            return;
        }

        _ = LoadClassesAsync(value.Handle);
    }

    partial void OnSelectedClassChanged(MonoClassItem? value)
    {
        CancelInstanceLookup();
        if (_suppressSelectionLoading || value is null || _client is null)
        {
            return;
        }

        _ = LoadClassDetailAsync(value.Handle);
    }

    partial void OnSelectedInstanceAddressChanged(string? value)
    {
        if (!_suppressInstanceAddressApply &&
            !string.IsNullOrWhiteSpace(value))
        {
            InstanceAddress = value;
            ResolveInstanceAddress();
        }
    }

    partial void OnClassSearchTextChanged(string value) =>
        ApplyClassFilter();

    private async Task DetectRuntimeForProcessAsync(
        int processId,
        bool forceRefresh = false)
    {
        if (processId <= 0)
        {
            ResetProcessState("未打开进程");
            return;
        }

        if (!forceRefresh &&
            ProcessId == processId &&
            HasSupportedRuntime)
        {
            return;
        }

        CancelRuntimeDetection();
        CancellationTokenSource cancellation = new();
        _runtimeDetectionCancellation = cancellation;
        int version = Interlocked.Increment(ref _runtimeDetectionVersion);
        ProcessId = 0;
        HasSupportedRuntime = false;
        IsIl2Cpp = false;
        IsLoading = true;
        StatusText = "正在检测 Mono/IL2CPP 运行时...";
        try
        {
            IReadOnlyList<string> moduleNames = await Task.Run(() =>
            {
                try
                {
                    return _session.EnumerateModules()
                        .Select(static module => module.Name)
                        .ToArray();
                }
                catch
                {
                    return Array.Empty<string>();
                }
            }, cancellation.Token).ConfigureAwait(true);

            if (version != _runtimeDetectionVersion ||
                cancellation.IsCancellationRequested)
            {
                return;
            }

            (bool hasRuntime, bool isIl2Cpp) = DetectRuntime(moduleNames);
            if (hasRuntime)
            {
                ProcessId = processId;
                HasSupportedRuntime = true;
                IsIl2Cpp = isIl2Cpp;
                StatusText = isIl2Cpp
                    ? "检测到 IL2CPP 运行时"
                    : "检测到 Mono 运行时";
            }
            else
            {
                StatusText = "未检测到 Mono/IL2CPP 运行时";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (version == _runtimeDetectionVersion)
            {
                StatusText = "运行时检测失败: " + exception.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_runtimeDetectionCancellation, cancellation))
            {
                _runtimeDetectionCancellation = null;
                IsLoading = false;
            }

            cancellation.Dispose();
        }
    }

    private async Task LoadImagesAsync()
    {
        MonoDataCollectorClient? client = _client;
        if (client is null)
        {
            return;
        }

        IsLoading = true;
        try
        {
            Images.Clear();
            IReadOnlyList<MonoImageInfo> images = await Task.Run(
                    client.GetImages,
                    CancellationToken.None)
                .ConfigureAwait(true);
            foreach (MonoImageInfo image in images)
            {
                Images.Add(new MonoImageItem(image.Handle, image.Name));
            }

            StatusText = $"已加载{Images.Count}个程序集";
            ExportSelectedImageCommand.NotifyCanExecuteChanged();
            ApplyAssemblySymbolsCommand.NotifyCanExecuteChanged();
            if (Images.Count > 0)
            {
                SelectedImage = Images[0];
            }
            else
            {
                ClassesFilteredClear();
            }
        }
        catch (Exception exception)
        {
            StatusText = "程序集枚举错误: " + exception.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadClassesAsync(ulong imageHandle)
    {
        MonoDataCollectorClient? client = _client;
        if (client is null)
        {
            return;
        }

        IsLoading = true;
        _suppressSelectionLoading = true;
        try
        {
            SelectedClass = null;
            ClearClassDetail();
            IReadOnlyList<MonoClassInfo> classes = await Task.Run(
                    () => client.GetClasses(imageHandle),
                    CancellationToken.None)
                .ConfigureAwait(true);

            _suppressSelectionLoading = false;
            ClassesFilteredClear();
            foreach (MonoClassInfo classInfo in classes)
            {
                AddClass(new MonoClassItem(
                    classInfo.Handle,
                    classInfo.Name,
                    classInfo.Namespace,
                    classInfo.FullName));
            }

            ApplyClassFilter();
            StatusText = $"{SelectedImage?.Name} - {FilteredClasses.Count} 个类";
        }
        catch (Exception exception)
        {
            StatusText = "类枚举错误: " + exception.Message;
        }
        finally
        {
            _suppressSelectionLoading = false;
            IsLoading = false;
        }
    }

    private async Task LoadClassDetailAsync(ulong classHandle)
    {
        MonoDataCollectorClient? client = _client;
        if (client is null)
        {
            return;
        }

        IsLoading = true;
        ClearClassDetail();
        try
        {
            List<MonoFieldInfo> fields = await Task.Run(
                    () => client.GetFields(classHandle, includeInherited: true).ToList(),
                    CancellationToken.None)
                .ConfigureAwait(true);
            List<MonoMethodInfo> methods = await Task.Run(
                    () => client.GetMethods(classHandle, includeInherited: true).ToList(),
                    CancellationToken.None)
                .ConfigureAwait(true);
            IReadOnlyList<ulong> inheritance = await Task.Run(
                    () => BuildInheritanceHandles(client, classHandle),
                    CancellationToken.None)
                .ConfigureAwait(true);

            ClassInheritanceChain = BuildInheritanceChain(client, inheritance);
            foreach (ulong parent in inheritance)
            {
                Inheritance.Add(new MonoInheritanceItem(
                    parent,
                    client.GetClassName(parent) ?? "?"));
            }

            foreach (MonoFieldInfo field in fields)
            {
                if (!field.IsStatic && !field.IsLiteral)
                {
                    Fields.Add(CreateFieldRow(
                        client,
                        field,
                        level: 0,
                        parent: null,
                        valueOffset: field.Offset));
                }
            }

            MonoFieldRow[] sortedFields = Fields
                .OrderBy(static field => field.Offset)
                .ToArray();
            Fields.Clear();
            foreach (MonoFieldRow field in sortedFields)
            {
                Fields.Add(field);
            }
            ClassInheritanceChain = BuildInheritanceChain(client, inheritance);

            bool isEnum = client.IsClassEnum(classHandle);
            ulong staticBase = client.GetStaticFieldAddress(0, classHandle);
            foreach (MonoFieldInfo field in fields)
            {
                if (!ShouldShowStaticField(field, isEnum))
                {
                    continue;
                }

                ulong vtable = 0;
                if (!IsIl2Cpp)
                {
                    vtable = client.GetVTable(0, field.DeclaringClassHandle);
                }

                StaticFields.Add(new MonoStaticFieldRow
                {
                    Name = field.Name,
                    TypeName = field.TypeName,
                    Address = FormatStaticAddress(staticBase, field),
                    Value = ReadStaticFieldValue(
                        client,
                        vtable,
                        staticBase,
                        field,
                        isEnum)
                });
            }

            foreach (MonoMethodInfo method in methods)
            {
                MonoMethodSignature? signature = client.GetMethodSignature(method.Handle);
                Methods.Add(new MonoMethodRow
                {
                    Name = method.Name,
                    Parameters = FormatMethodParameters(signature),
                    Handle = method.Handle,
                    ClassName = SelectedClass?.DisplayName ?? string.Empty
                });
            }

            ApplyInstanceAddressIfAvailable();
        }
        catch (Exception exception)
        {
            StatusText = "类信息读取错误: " + exception.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static IReadOnlyList<ulong> BuildInheritanceHandles(
        MonoDataCollectorClient client,
        ulong classHandle)
    {
        List<ulong> result = [];
        HashSet<ulong> visited = [];
        for (ulong current = classHandle;
             current != 0 && visited.Add(current);
             current = client.GetParentClass(current))
        {
            result.Add(current);
        }

        result.Reverse();
        return result;
    }

    private static string BuildInheritanceChain(
        MonoDataCollectorClient client,
        IReadOnlyList<ulong> inheritance)
    {
        return string.Join(
            " -> ",
            inheritance.Select(handle =>
                client.GetFullTypeName(handle) ??
                client.GetClassName(handle) ??
                "?"));
    }

    private static bool ShouldShowStaticField(
        MonoFieldInfo field,
        bool classIsEnum)
    {
        if (!field.IsStatic)
        {
            return false;
        }

        return !field.IsLiteral || classIsEnum;
    }

    private static string FormatStaticAddress(
        ulong staticBase,
        MonoFieldInfo field)
    {
        if (field.IsLiteral)
        {
            return "?";
        }

        if (staticBase == 0)
        {
            return "?";
        }

        ulong address = AddOffset(staticBase, field.Offset) ?? 0;
        return address == 0 ? "?" : address.ToString("X");
    }

    private static string ReadStaticFieldValue(
        MonoDataCollectorClient client,
        ulong vtable,
        ulong staticBase,
        MonoFieldInfo field,
        bool classIsEnum)
    {
        try
        {
            if (!client.IsIl2Cpp && vtable == 0 && !field.IsLiteral)
            {
                return "?";
            }

            ulong raw = client.GetStaticFieldValue(vtable, field.Handle);
            if (classIsEnum && field.IsLiteral)
            {
                return ((uint)raw).ToString("X8", CultureInfo.InvariantCulture);
            }

            return FormatFieldValue(field.TypeCode, raw);
        }
        catch
        {
            return "?";
        }
    }

    private static string FormatFieldValue(MonoTypeCode type, ulong raw)
    {
        return type switch
        {
            MonoTypeCode.Boolean => raw == 0 ? "false" : "true",
            MonoTypeCode.I1 => ((sbyte)(byte)raw).ToString(CultureInfo.InvariantCulture),
            MonoTypeCode.U1 => ((byte)raw).ToString(CultureInfo.InvariantCulture),
            MonoTypeCode.Char => ((ushort)raw).ToString(CultureInfo.InvariantCulture),
            MonoTypeCode.I2 => ((short)(ushort)raw).ToString(CultureInfo.InvariantCulture),
            MonoTypeCode.U2 => ((ushort)raw).ToString(CultureInfo.InvariantCulture),
            MonoTypeCode.I4 => ((int)raw).ToString(CultureInfo.InvariantCulture),
            MonoTypeCode.U4 => ((uint)raw).ToString(CultureInfo.InvariantCulture),
            MonoTypeCode.I8 => ((long)raw).ToString(CultureInfo.InvariantCulture),
            MonoTypeCode.U8 => raw.ToString(CultureInfo.InvariantCulture),
            MonoTypeCode.R4 => BitConverter.Int32BitsToSingle(
                    unchecked((int)raw))
                .ToString("G9", CultureInfo.InvariantCulture),
            MonoTypeCode.R8 => BitConverter.Int64BitsToDouble(
                    unchecked((long)raw))
                .ToString("G17", CultureInfo.InvariantCulture),
            MonoTypeCode.String or
            MonoTypeCode.Pointer or
            MonoTypeCode.ByReference or
            MonoTypeCode.ValueType or
            MonoTypeCode.Class or
            MonoTypeCode.Array or
            MonoTypeCode.GenericInstance or
            MonoTypeCode.IntPtr or
            MonoTypeCode.UIntPtr or
            MonoTypeCode.FunctionPointer or
            MonoTypeCode.Object or
            MonoTypeCode.SingleDimensionalArray => FormatPointer(raw),
            _ => $"0x{raw:X}"
        };
    }

    private static string FormatPointer(ulong value) =>
        value == 0 ? "null" : $"0x{value:X}";

    private MonoFieldRow CreateFieldRow(
        MonoDataCollectorClient client,
        MonoFieldInfo field,
        int level,
        MonoFieldRow? parent,
        int valueOffset)
    {
        bool canHaveChildren = CanFieldHaveChildren(field.TypeCode);
        ulong typeClass = canHaveChildren
            ? ResolveFieldTypeClass(client, field)
            : 0;
        return new MonoFieldRow
        {
            Offset = field.Offset,
            ValueOffset = valueOffset,
            Name = field.Name,
            TypeName = field.TypeName,
            FieldHandle = field.Handle,
            MonoType = field.TypeCode,
            IsInlineValueType = IsInlineValueType(
                client,
                field.TypeCode,
                typeClass),
            IsExpandable = canHaveChildren && typeClass != 0,
            TypeClassHandle = typeClass,
            Level = level,
            Parent = parent
        };
    }

    private static bool CanFieldHaveChildren(MonoTypeCode type) =>
        type is MonoTypeCode.Pointer or
            MonoTypeCode.ValueType or
            MonoTypeCode.Class or
            MonoTypeCode.GenericInstance or
            MonoTypeCode.Object;

    private static bool IsInlineValueType(
        MonoDataCollectorClient client,
        MonoTypeCode type,
        ulong typeClass)
    {
        if (type == MonoTypeCode.ValueType)
        {
            return true;
        }

        if (type != MonoTypeCode.GenericInstance || typeClass == 0)
        {
            return false;
        }

        try
        {
            return client.IsClassValueType(typeClass);
        }
        catch
        {
            return false;
        }
    }

    private static ulong ResolveFieldTypeClass(
        MonoDataCollectorClient client,
        MonoFieldInfo field)
    {
        try
        {
            ulong classHandle = client.GetFieldClass(field.Handle);
            if (classHandle == 0 && field.TypeHandle != 0)
            {
                classHandle = client.GetClassFromType(field.TypeHandle);
            }

            if (field.TypeCode != MonoTypeCode.Pointer || classHandle == 0)
            {
                return classHandle;
            }

            ulong classType = client.GetClassType(classHandle);
            ulong pointerType = client.GetPointerType(classType);
            ulong pointerClass = client.GetClassFromType(pointerType);
            if (pointerClass == 0)
            {
                pointerClass = client.GetPointerClass(pointerType);
            }

            if (pointerClass != 0 &&
                client.GetTypeCode(pointerType) == MonoTypeCode.GenericInstance)
            {
                ulong parent = client.GetParentClass(pointerClass);
                if (parent != 0)
                {
                    pointerClass = parent;
                }
            }

            return pointerClass != 0 ? pointerClass : classHandle;
        }
        catch
        {
            return 0;
        }
    }

    private List<MonoFieldRow> BuildChildFields(
        MonoDataCollectorClient client,
        MonoFieldRow parent)
    {
        List<MonoFieldRow> result = [];
        IReadOnlyList<MonoFieldInfo> fields = client.GetFields(
            parent.TypeClassHandle,
            includeInherited: false);
        foreach (MonoFieldInfo field in fields)
        {
            if (field.IsStatic || field.IsLiteral)
            {
                continue;
            }

            int valueOffset = parent.IsInlineValueType
                ? GetChildValueOffset(parent, fields, field)
                : field.Offset;
            MonoFieldRow row = new()
            {
                Offset = field.Offset,
                ValueOffset = valueOffset,
                Name = field.Name,
                TypeName = field.TypeName,
                FieldHandle = field.Handle,
                MonoType = field.TypeCode,
                IsInlineValueType = IsInlineValueType(
                    client,
                    field.TypeCode,
                    ResolveFieldTypeClass(client, field)),
                IsExpandable = CanFieldHaveChildren(field.TypeCode),
                TypeClassHandle = ResolveFieldTypeClass(client, field),
                Level = parent.Level + 1,
                Parent = parent
            };
            result.Add(row);
        }

        result.Sort(static (left, right) =>
            left.Offset.CompareTo(right.Offset));
        return result;
    }

    private int GetChildValueOffset(
        MonoFieldRow parent,
        IReadOnlyList<MonoFieldInfo> siblingFields,
        MonoFieldInfo field)
    {
        if (!parent.IsInlineValueType)
        {
            return field.Offset;
        }

        int headerSize = _session.Target.Is64Bit ? 16 : 8;
        int minimumOffset = siblingFields
            .Where(static item => !item.IsStatic && !item.IsLiteral)
            .Select(static item => item.Offset)
            .DefaultIfEmpty(field.Offset)
            .Min();
        return minimumOffset == headerSize && field.Offset >= headerSize
            ? field.Offset - headerSize
            : field.Offset;
    }

    private void CollapseField(MonoFieldRow parent)
    {
        int index = Fields.IndexOf(parent);
        if (index < 0)
        {
            return;
        }

        int descendantCount = 0;
        for (int current = index + 1;
             current < Fields.Count &&
             Fields[current].Level > parent.Level;
             current++)
        {
            descendantCount++;
        }

        for (int count = 0; count < descendantCount; count++)
        {
            Fields.RemoveAt(index + 1);
        }

        parent.LoadedChildCount = 0;
        parent.IsExpanded = false;
    }

    private void ApplyInstanceAddressIfAvailable()
    {
        if (TryResolveAddress(InstanceAddress, out ulong address) &&
            address != 0)
        {
            ApplyInstanceAddress(address);
        }
    }

    private bool TryResolveAddress(string text, out ulong address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string candidate = text.Trim();
        int plus = candidate.LastIndexOf('+');
        if (plus > 0 && plus < candidate.Length - 1)
        {
            string moduleName = candidate[..plus].Trim().Trim('"');
            string offsetText = candidate[(plus + 1)..].Trim();
            if (TryParseHex(offsetText, out ulong offset))
            {
                ModuleDescriptor? module = _session.EnumerateModules()
                    .FirstOrDefault(item =>
                        item.Name.Equals(
                            moduleName,
                            StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(item.FilePath).Equals(
                            moduleName,
                            StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileNameWithoutExtension(item.Name).Equals(
                            moduleName,
                            StringComparison.OrdinalIgnoreCase));
                if (module is not null)
                {
                    address = module.BaseAddress + offset;
                    return true;
                }
            }
        }

        return TryParseHex(candidate, out address);
    }

    private static bool TryParseHex(string text, out ulong value)
    {
        value = 0;
        string candidate = text.Trim();
        if (candidate.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[2..];
        }
        else if (candidate.StartsWith('$'))
        {
            candidate = candidate[1..];
        }

        return candidate.Length > 0 &&
               ulong.TryParse(
                   candidate,
                   NumberStyles.HexNumber,
                   CultureInfo.InvariantCulture,
                   out value);
    }

    private void ApplyInstanceAddress(ulong instanceAddress)
    {
        MonoDataCollectorClient? client = _client;
        if (client is null || instanceAddress == 0)
        {
            return;
        }

        foreach (MonoFieldRow row in Fields)
        {
            row.Value = ReadInstanceFieldValue(
                client,
                instanceAddress,
                row);
        }
    }

    private string ReadInstanceFieldValue(
        MonoDataCollectorClient client,
        ulong instanceAddress,
        MonoFieldRow field)
    {
        ulong? storageAddress = ResolveFieldStorageAddress(
            instanceAddress,
            field);
        if (storageAddress is null)
        {
            return field.Parent is null ? "?" : "null";
        }

        if (IsStringField(field))
        {
            return ReadStringField(storageAddress.Value);
        }

        if (field.IsInlineValueType)
        {
            return $"inline 0x{storageAddress.Value:X}";
        }

        if (IsPointerLike(field.MonoType))
        {
            ulong? value = ReadPointer(storageAddress.Value);
            return value is null ? "?" : FormatPointer(value.Value);
        }

        return ReadPrimitiveFieldValue(storageAddress.Value, field.MonoType);
    }

    private ulong? ResolveFieldStorageAddress(
        ulong instanceAddress,
        MonoFieldRow field)
    {
        if (field.Parent is null)
        {
            return AddOffset(instanceAddress, field.ValueOffset);
        }

        ulong? parentAddress = ResolveFieldStorageAddress(
            instanceAddress,
            field.Parent);
        if (parentAddress is null)
        {
            return null;
        }

        ulong? baseAddress = field.Parent.IsInlineValueType
            ? parentAddress
            : ReadPointer(parentAddress.Value);
        return baseAddress is null or 0
            ? null
            : AddOffset(baseAddress.Value, field.ValueOffset);
    }

    private static ulong? AddOffset(ulong address, int offset)
    {
        if (offset >= 0)
        {
            ulong value = (ulong)offset;
            return address <= ulong.MaxValue - value
                ? address + value
                : null;
        }

        ulong absolute = unchecked((ulong)(-(long)offset));
        return address >= absolute ? address - absolute : null;
    }

    private string ReadPrimitiveFieldValue(
        ulong address,
        MonoTypeCode type)
    {
        int size = GetTypeSize(type);
        if (size <= 0 || size > sizeof(ulong))
        {
            return "?";
        }

        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        Span<byte> target = buffer[..size];
        if (!_session.Target.TryReadBytes(address, target))
        {
            return "?";
        }

        ulong raw = size switch
        {
            1 => buffer[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(buffer),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(buffer),
            8 => BinaryPrimitives.ReadUInt64LittleEndian(buffer),
            _ => 0
        };
        return FormatFieldValue(type, raw);
    }

    private int GetTypeSize(MonoTypeCode type) =>
        type switch
        {
            MonoTypeCode.Boolean or
            MonoTypeCode.I1 or
            MonoTypeCode.U1 => 1,
            MonoTypeCode.Char or
            MonoTypeCode.I2 or
            MonoTypeCode.U2 => 2,
            MonoTypeCode.I4 or
            MonoTypeCode.U4 or
            MonoTypeCode.R4 => 4,
            MonoTypeCode.I8 or
            MonoTypeCode.U8 or
            MonoTypeCode.R8 => 8,
            MonoTypeCode.IntPtr or
            MonoTypeCode.UIntPtr => _session.Target.Is64Bit ? 8 : 4,
            _ => 0
        };

    private string ReadStringField(ulong storageAddress)
    {
        ulong? stringObject = ReadPointer(storageAddress);
        if (stringObject is null)
        {
            return "?";
        }

        if (stringObject.Value == 0)
        {
            return "null";
        }

        string? value = ReadMonoString(stringObject.Value);
        return value is null
            ? "?"
            : "\"" + EscapeString(value) + "\"";
    }

    private string? ReadMonoString(ulong stringObject)
    {
        bool is64Bit = _session.Target.Is64Bit;
        ulong lengthAddress = stringObject + (uint)(is64Bit ? 16 : 8);
        Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
        if (!_session.Target.TryReadBytes(lengthAddress, lengthBytes))
        {
            return null;
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length < 0 || length > MaximumMonoStringLength)
        {
            return null;
        }

        if (length == 0)
        {
            return string.Empty;
        }

        byte[] data = new byte[length * sizeof(char)];
        ulong dataAddress = stringObject + (uint)(is64Bit ? 20 : 16);
        return _session.Target.TryReadBytes(dataAddress, data)
            ? Encoding.Unicode.GetString(data)
            : null;
    }

    private ulong? ReadPointer(ulong address)
    {
        int size = _session.Target.Is64Bit ? sizeof(ulong) : sizeof(uint);
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        Span<byte> target = buffer[..size];
        if (!_session.Target.TryReadBytes(address, target))
        {
            return null;
        }

        return size == sizeof(ulong)
            ? BinaryPrimitives.ReadUInt64LittleEndian(buffer)
            : BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static bool IsStringField(MonoFieldRow field) =>
        field.MonoType == MonoTypeCode.String ||
        field.TypeName.Equals(
            "System.String",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsPointerLike(MonoTypeCode type) =>
        type is MonoTypeCode.String or
            MonoTypeCode.Pointer or
            MonoTypeCode.ByReference or
            MonoTypeCode.Class or
            MonoTypeCode.Array or
            MonoTypeCode.GenericInstance or
            MonoTypeCode.IntPtr or
            MonoTypeCode.UIntPtr or
            MonoTypeCode.FunctionPointer or
            MonoTypeCode.Object or
            MonoTypeCode.SingleDimensionalArray;

    private static string EscapeString(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

    private static string FormatMethodParameters(MonoMethodSignature? signature)
    {
        if (signature is null)
        {
            return string.Empty;
        }

        List<string> parameters = [];
        for (int index = 0;
             index < signature.ParameterTypes.Count &&
             index < signature.ParameterNames.Count;
             index++)
        {
            string type = signature.ParameterTypes[index].Trim();
            string name = signature.ParameterNames[index].Trim();
            parameters.Add(string.IsNullOrWhiteSpace(name)
                ? type
                : $"{type} {name}");
        }

        return $"{signature.ReturnType} ({string.Join(", ", parameters)})";
    }

    private void ApplyClassFilter()
    {
        string search = ClassSearchText.Trim();
        MonoClassItem? selected = SelectedClass;
        ClassesFilteredClear();
        foreach (MonoClassItem item in _allClasses)
        {
            if (string.IsNullOrEmpty(search) ||
                item.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                item.Namespace.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                item.FullName.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                FilteredClasses.Add(item);
            }
        }

        if (selected is not null && FilteredClasses.Contains(selected))
        {
            SelectedClass = selected;
        }
    }

    private readonly List<MonoClassItem> _allClasses = [];

    private void ClassesFilteredClear()
    {
        _allClasses.Clear();
        FilteredClasses.Clear();
        SelectedClass = null;
    }

    private void AddClass(MonoClassItem item)
    {
        _allClasses.Add(item);
        FilteredClasses.Add(item);
    }

    private void ClearClassDetail()
    {
        ClassInheritanceChain = string.Empty;
        StaticFields.Clear();
        Fields.Clear();
        Methods.Clear();
        Inheritance.Clear();
        InstanceAddresses.Clear();
        ClearInstanceSelection();
    }

    private void ClearInstanceSelection()
    {
        _suppressInstanceAddressApply = true;
        try
        {
            InstanceAddress = string.Empty;
            SelectedInstanceAddress = null;
        }
        finally
        {
            _suppressInstanceAddressApply = false;
        }

        ClearFieldValues();
    }

    private void ClearFieldValues()
    {
        foreach (MonoFieldRow row in Fields)
        {
            row.Value = string.Empty;
        }
    }

    private void ClearBrowserState()
    {
        _suppressSelectionLoading = true;
        try
        {
            Images.Clear();
            ClassesFilteredClear();
            ClearClassDetail();
            SelectedImage = null;
            SelectedClass = null;
        }
        finally
        {
            _suppressSelectionLoading = false;
        }
    }

    private void ResetProcessState(string status)
    {
        Interlocked.Increment(ref _runtimeDetectionVersion);
        HasSupportedRuntime = false;
        IsConnected = false;
        IsIl2Cpp = false;
        IsLoading = false;
        ProcessId = 0;
        ClearBrowserState();
        StatusText = status;
    }

    private void CancelRuntimeDetection()
    {
        CancellationTokenSource? cancellation = _runtimeDetectionCancellation;
        _runtimeDetectionCancellation = null;
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    private void CancelInstanceLookup()
    {
        CancellationTokenSource? cancellation = _instanceLookupCancellation;
        _instanceLookupCancellation = null;
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    private void DisposeClient()
    {
        _client?.Dispose();
        _client = null;
    }

    private static (bool HasRuntime, bool IsIl2Cpp) DetectRuntime(
        IReadOnlyList<string> moduleNames)
    {
        bool hasMono = moduleNames.Any(name =>
            MonoRuntimeModuleNames.Any(candidate =>
                name.Contains(candidate, StringComparison.OrdinalIgnoreCase)));
        bool hasIl2Cpp = moduleNames.Any(name =>
            Il2CppRuntimeModuleNames.Any(candidate =>
                name.Contains(candidate, StringComparison.OrdinalIgnoreCase)));
        return (hasMono || hasIl2Cpp, hasIl2Cpp);
    }

    private static string GetSafeFileName(string fileName)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string result = new(fileName
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());
        return string.IsNullOrWhiteSpace(result)
            ? "MonoImageDump"
            : result;
    }
}
