using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DogeDebugger.Core.Modules;
using DogeDebugger.Debugger.Session;
using DogeDebugger.Plugins.UnrealEngine.Models;
using DogeDebugger.Plugins.UnrealEngine.Services;
using Microsoft.Win32;

namespace DogeDebugger.Plugins.UnrealEngine.UI;

public partial class UnrealExplorerViewModel : ObservableObject, IDisposable
{
    private readonly DebuggerSession _session;
    private readonly UnrealEngineService _service;
    private readonly Action<ulong> _navigateToAddress;
    private readonly Action<ulong, string>? _registerSymbol;
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _scanCancellation;
    private bool _disposed;

    [ObservableProperty]
    private ModuleDescriptor? _selectedModule;

    [ObservableProperty]
    private UnrealObjectInfo? _selectedPackage;

    [ObservableProperty]
    private UnrealObjectInfo? _selectedObject;

    [ObservableProperty]
    private UnrealTypeInfo? _selectedType;

    [ObservableProperty]
    private UnrealWorldActorInfo? _selectedActor;

    [ObservableProperty]
    private UnrealObjectArrayKind _objectArrayKind = UnrealObjectArrayKind.Fixed;

    [ObservableProperty]
    private string _manualNamePool = string.Empty;

    [ObservableProperty]
    private string _manualNamePoolBlocks = string.Empty;

    [ObservableProperty]
    private string _manualGObjects = string.Empty;

    [ObservableProperty]
    private string _manualGWorld = string.Empty;

    [ObservableProperty]
    private string _manualGEngine = string.Empty;

    [ObservableProperty]
    private string _addressText = string.Empty;

    [ObservableProperty]
    private bool _useCache = true;

    [ObservableProperty]
    private bool _includeInheritedMembers = true;

    [ObservableProperty]
    private string _statusText = "尚未扫描 Unreal Engine。";

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _hasSession;

    [ObservableProperty]
    private bool _hasDiagnostics;

    public UnrealExplorerViewModel(
        DebuggerSession session,
        Action<ulong> navigateToAddress,
        Action<ulong, string>? registerSymbol = null)
    {
        _session = session;
        _service = session.UnrealEngine;
        _navigateToAddress = navigateToAddress;
        _registerSymbol = registerSymbol;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _service.Changed += HandleServiceChanged;
    }

    public ObservableCollection<ModuleDescriptor> Modules { get; } = [];

    public ObservableCollection<UnrealObjectInfo> Packages { get; } = [];

    public ObservableCollection<UnrealObjectInfo> Objects { get; } = [];

    public ObservableCollection<UnrealTypeInfo> Types { get; } = [];

    public ObservableCollection<UnrealWorldActorInfo> Actors { get; } = [];

    public ObservableCollection<UnrealOffsetRow> Offsets { get; } = [];

    public ObservableCollection<UnrealDiagnosticEntry> Diagnostics { get; } = [];

    public ObservableCollection<UnrealMemberValueEntry> ObjectValues { get; } = [];

    public ObservableCollection<UnrealMemberInfo> TypeMembers { get; } = [];

    public ObservableCollection<UnrealMemberInfo> TypeFunctions { get; } = [];

    public ObservableCollection<UnrealEnumEntry> EnumEntries { get; } = [];

    public ObservableCollection<UnrealInheritanceInfo> Inheritance { get; } = [];

    public IReadOnlyList<UnrealObjectArrayKind> ObjectArrayKinds { get; } =
        Enum.GetValues<UnrealObjectArrayKind>();

    public void OnTargetChanged()
    {
        Modules.Clear();
        foreach (ModuleDescriptor module in _service.GetCandidateModules())
        {
            Modules.Add(module);
        }

        SelectedModule = Modules.FirstOrDefault();
        ClearSessionView();
        StatusText = _session.Target.IsOpen
            ? Modules.Count == 0
                ? "未找到可扫描模块。"
                : "检测到目标进程，可开始 Unreal Engine 扫描。"
            : "尚未打开目标进程。";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        _service.Changed -= HandleServiceChanged;
        GC.SuppressFinalize(this);
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (!_session.Target.IsOpen)
        {
            StatusText = "请先打开目标进程。";
            return;
        }

        if (IsScanning)
        {
            return;
        }

        _scanCancellation?.Dispose();
        _scanCancellation = new CancellationTokenSource();
        IsScanning = true;
        Progress = 0;
        ProgressText = "准备扫描";
        StatusText = "正在扫描 Unreal Engine...";
        try
        {
            Progress<UnrealScanProgress> progress = new(value =>
            {
                void UpdateProgress()
                {
                    Progress = value.Progress;
                    ProgressText = $"{value.Stage}: {value.Message}";
                }

                if (_dispatcher.CheckAccess())
                {
                    UpdateProgress();
                }
                else
                {
                    _dispatcher.BeginInvoke(UpdateProgress);
                }
            });

            UnrealSession session = await _service.ScanAsync(
                    BuildScanOptions(),
                    progress,
                    _scanCancellation.Token)
                .ConfigureAwait(true);
            ApplySession(session);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Unreal Engine 扫描已取消。";
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }
        finally
        {
            IsScanning = false;
            Progress = 1;
        }
    }

    [RelayCommand]
    private void CancelScan()
    {
        _scanCancellation?.Cancel();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await ScanAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RescanAsync()
    {
        await ScanAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ExportOffsetsAsync()
    {
        UnrealSession? session = _service.Current;
        if (session is null)
        {
            return;
        }

        SaveFileDialog dialog = new()
        {
            Title = "导出 Unreal offsets",
            Filter = "JSON (*.json)|*.json|Text (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = $"{SafeFileName(session.Module.Name)}.ue-offsets.json"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await File.WriteAllTextAsync(
                dialog.FileName,
                System.Text.Json.JsonSerializer.Serialize(
                    session.Offsets,
                    new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = true
                    }),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            .ConfigureAwait(true);
        StatusText = $"已导出 offsets: {dialog.FileName}";
    }

    [RelayCommand]
    private async Task ExportNamesAsync()
    {
        UnrealSession? session = _service.Current;
        if (session is null)
        {
            return;
        }

        SaveFileDialog dialog = new()
        {
            Title = "导出 Unreal names",
            Filter = "Text (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = $"{SafeFileName(session.Module.Name)}.ue-names.txt"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string text = string.Join(
            Environment.NewLine,
            session.Objects.Select(item => $"{item.IndexHex} {item.Name}"));
        await File.WriteAllTextAsync(
                dialog.FileName,
                text,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            .ConfigureAwait(true);
        StatusText = $"已导出 names: {dialog.FileName}";
    }

    [RelayCommand]
    private async Task ExportObjectsAsync()
    {
        UnrealSession? session = _service.Current;
        if (session is null)
        {
            return;
        }

        SaveFileDialog dialog = new()
        {
            Title = "导出 Unreal objects",
            Filter = "Text (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = $"{SafeFileName(session.Module.Name)}.ue-objects.txt"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string text = $"Count: {session.ObjectCount}{Environment.NewLine}" +
                      string.Join(
                          Environment.NewLine,
                          session.Objects.Select(item =>
                              $"[{item.IndexHex}] {{{item.AddressHex}}} {item.FullName}"));
        await File.WriteAllTextAsync(
                dialog.FileName,
                text,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            .ConfigureAwait(true);
        StatusText = $"已导出 objects: {dialog.FileName}";
    }

    [RelayCommand]
    private void NavigateSelectedObject()
    {
        if (SelectedObject?.Address is > 0)
        {
            _navigateToAddress(SelectedObject.Address);
        }
    }

    [RelayCommand]
    private void NavigateSelectedActor()
    {
        if (SelectedActor?.Address is > 0)
        {
            _navigateToAddress(SelectedActor.Address);
        }
    }

    [RelayCommand]
    private void OpenAddress()
    {
        if (!TryParseAddress(AddressText, out ulong address) || address == 0)
        {
            StatusText = "地址格式无效";
            return;
        }

        UnrealObjectInfo? item = _service.FindObject(address);
        if (item is not null)
        {
            SelectedObject = item;
            SelectedType = _service.FindType(address) ??
                           Types.FirstOrDefault(type => ReferenceEquals(
                               type.Object,
                               item));
            StatusText = $"已打开对象 {item.FullName}";
            return;
        }

        _navigateToAddress(address);
        StatusText = $"无法按 UObject 解析，已跳转到 0x{address:X}";
    }

    [RelayCommand]
    private void ApplyFunctionSymbols()
    {
        int count = 0;
        foreach (UnrealTypeInfo type in Types)
        {
            foreach (UnrealMemberInfo function in
                     _service.GetFunctions(type.Object))
            {
                if (function.NativeAddress == 0)
                {
                    continue;
                }

                string symbolName = string.IsNullOrWhiteSpace(function.DeclaringType)
                    ? function.Name
                    : $"{function.DeclaringType}.{function.Name}";
                _registerSymbol?.Invoke(function.NativeAddress, symbolName);
                count++;
            }
        }

        StatusText = $"已应用 {count:N0} 个 UE原生方法符号";
    }

    private UnrealScanOptions BuildScanOptions() => new()
    {
        ModuleName = SelectedModule?.Name,
        UseCache = UseCache,
        AutoDetect = true,
        PersistCache = true,
        NamePoolAddress = ParseAddress(ManualNamePool),
        NamePoolBlockArrayAddress = ParseAddress(ManualNamePoolBlocks),
        GObjectsAddress = ParseAddress(ManualGObjects),
        ObjectArrayKind = ObjectArrayKind,
        GWorldAddress = ParseAddress(ManualGWorld),
        GEngineAddress = ParseAddress(ManualGEngine)
    };

    private void ApplySession(UnrealSession session)
    {
        Packages.Clear();
        Objects.Clear();
        Types.Clear();
        Actors.Clear();
        Offsets.Clear();
        Diagnostics.Clear();

        foreach (UnrealObjectInfo package in session.Packages)
        {
            Packages.Add(package);
        }

        foreach (UnrealObjectInfo item in session.Objects)
        {
            Objects.Add(item);
        }

        foreach (UnrealTypeInfo type in session.Types)
        {
            Types.Add(type);
        }

        foreach (UnrealWorldActorInfo actor in _service.ReadWorldActors())
        {
            Actors.Add(actor);
        }

        foreach (UnrealOffsetRow row in CreateOffsetRows(session.Offsets))
        {
            Offsets.Add(row);
        }

        foreach (UnrealDiagnosticEntry entry in session.Diagnostics.Entries)
        {
            Diagnostics.Add(entry);
        }
        HasDiagnostics = Diagnostics.Count > 0;

        SelectedPackage = Packages.FirstOrDefault();
        SelectedObject = Objects.FirstOrDefault();
        SelectedType = Types.FirstOrDefault();
        SelectedActor = Actors.FirstOrDefault();
        RefreshSelectedObjectValues();
        RefreshSelectedTypeDetails();
        HasSession = true;
        StatusText = session.Diagnostics.HasProblems
            ? $"Unreal 会话不完整：对象 {Objects.Count:N0}，类型 {Types.Count:N0}。"
            : $"Unreal 会话就绪：对象 {Objects.Count:N0}，类型 {Types.Count:N0}。";
    }

    private void ClearSessionView()
    {
        Packages.Clear();
        Objects.Clear();
        Types.Clear();
        Actors.Clear();
        Offsets.Clear();
        Diagnostics.Clear();
        ObjectValues.Clear();
        TypeMembers.Clear();
        TypeFunctions.Clear();
        EnumEntries.Clear();
        Inheritance.Clear();
        HasDiagnostics = false;
        SelectedPackage = null;
        SelectedObject = null;
        SelectedType = null;
        SelectedActor = null;
        HasSession = false;
    }

    partial void OnSelectedObjectChanged(UnrealObjectInfo? value)
    {
        if (value is not null)
        {
            AddressText = value.AddressHex;
        }

        RefreshSelectedObjectValues();
    }

    partial void OnSelectedTypeChanged(UnrealTypeInfo? value)
    {
        RefreshSelectedTypeDetails();
    }

    private void RefreshSelectedObjectValues()
    {
        ObjectValues.Clear();
        if (SelectedObject is null)
        {
            return;
        }

        foreach (UnrealMemberValueEntry entry in _service.ReadObjectValues(
                     SelectedObject,
                     options: new UnrealValueReadOptions
                     {
                         MaxStringLength = 256,
                         ArrayPreview = 8,
                         StructDepth = 1
                     }))
        {
            ObjectValues.Add(entry);
        }
    }

    private void RefreshSelectedTypeDetails()
    {
        TypeMembers.Clear();
        TypeFunctions.Clear();
        EnumEntries.Clear();
        Inheritance.Clear();
        if (SelectedType is null)
        {
            return;
        }

        foreach (UnrealMemberInfo member in _service.GetMembers(
                     SelectedType.Object,
                     IncludeInheritedMembers))
        {
            TypeMembers.Add(member);
        }

        foreach (UnrealMemberInfo function in _service.GetFunctions(SelectedType.Object))
        {
            TypeFunctions.Add(function);
        }

        foreach (UnrealInheritanceInfo inherited in
                 _service.GetInheritance(SelectedType.Object))
        {
            Inheritance.Add(inherited);
        }

        if (SelectedType.Kind == UnrealObjectKind.Enum)
        {
            foreach (UnrealEnumEntry entry in _service.GetEnumEntries(SelectedType.Object))
            {
                EnumEntries.Add(entry);
            }
        }
    }

    private void HandleServiceChanged()
    {
        if (_dispatcher.CheckAccess())
        {
            ApplySession(_service.Current ?? new UnrealSession
            {
                Module = SelectedModule ?? new ModuleDescriptor(),
                Offsets = new UnrealOffsets()
            });
            return;
        }

        _dispatcher.BeginInvoke(() =>
        {
            UnrealSession? session = _service.Current;
            if (session is not null)
            {
                ApplySession(session);
            }
        });
    }

    private static IReadOnlyList<UnrealOffsetRow> CreateOffsetRows(UnrealOffsets offsets) =>
    [
        new() { Name = "PointerSize", Value = offsets.PointerSize.ToString(CultureInfo.InvariantCulture) },
        new() { Name = "NamePoolKind", Value = offsets.NamePoolKind.ToString() },
        new() { Name = "NamePoolAddress", Value = FormatAddress(offsets.NamePoolAddress) },
        new() { Name = "NamePoolBlockArrayAddress", Value = FormatAddress(offsets.NamePoolBlockArrayAddress) },
        new() { Name = "GObjectsAddress", Value = FormatAddress(offsets.GObjectsAddress) },
        new() { Name = "ObjectArrayKind", Value = offsets.ObjectArrayKind.ToString() },
        new() { Name = "GWorldAddress", Value = FormatAddress(offsets.GWorldAddress) },
        new() { Name = "GEngineAddress", Value = FormatAddress(offsets.GEngineAddress) },
        new() { Name = "UObjectIndex", Value = $"0x{offsets.UObjectIndex:X}" },
        new() { Name = "UObjectClass", Value = $"0x{offsets.UObjectClass:X}" },
        new() { Name = "UObjectName", Value = $"0x{offsets.UObjectName:X}" },
        new() { Name = "UObjectOuter", Value = $"0x{offsets.UObjectOuter:X}" },
        new() { Name = "UStructSuperStruct", Value = $"0x{offsets.UStructSuperStruct:X}" },
        new() { Name = "UStructChildren", Value = $"0x{offsets.UStructChildren:X}" },
        new() { Name = "UStructChildProperties", Value = $"0x{offsets.UStructChildProperties:X}" },
        new() { Name = "UFunctionExecFunction", Value = $"0x{offsets.UFunctionExecFunction:X}" },
        new() { Name = "FPropertyObjectClassType", Value = $"0x{offsets.FPropertyObjectClassType:X}" },
        new() { Name = "FPropertyEnumField", Value = $"0x{offsets.FPropertyEnumField:X}" },
        new() { Name = "UEnumNames", Value = $"0x{offsets.UEnumNames:X}" },
        new() { Name = "UEnumNamesLayout", Value = offsets.UEnumNamesLayout.ToString() },
        new() { Name = "UWorldPersistentLevel", Value = $"0x{offsets.UWorldPersistentLevel:X}" },
        new() { Name = "ULevelActors", Value = $"0x{offsets.ULevelActors:X}" }
    ];

    private static string FormatAddress(ulong address) =>
        address == 0 ? string.Empty : $"0x{address:X}";

    private static ulong ParseAddress(string text)
    {
        text = text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return ulong.TryParse(
            text,
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out ulong value)
                ? value
                : 0;
    }

    private bool TryParseAddress(string text, out ulong address)
    {
        address = ParseAddress(text);
        if (address != 0)
        {
            return true;
        }

        text = text.Trim().Trim('"');
        int plus = text.LastIndexOf('+');
        if (plus <= 0 || plus >= text.Length - 1)
        {
            return false;
        }

        string moduleName = text[..plus].Trim();
        string offsetText = text[(plus + 1)..].Trim();
        if (offsetText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            offsetText = offsetText[2..];
        }

        if (!ulong.TryParse(
                offsetText,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out ulong offset))
        {
            return false;
        }

        ModuleDescriptor? module = Modules.FirstOrDefault(item =>
            item.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(item.FilePath).Equals(
                moduleName,
                StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileNameWithoutExtension(item.Name).Equals(
                moduleName,
                StringComparison.OrdinalIgnoreCase));
        if (module is null)
        {
            return false;
        }

        address = module.BaseAddress + offset;
        return true;
    }

    partial void OnIncludeInheritedMembersChanged(bool value)
    {
        RefreshSelectedTypeDetails();
    }

    private static string SafeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());
    }
}
