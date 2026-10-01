using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DogeDebugger.Core.Modules;
using DogeDebugger.Core.Search;
using DogeDebugger.Debugger.Session;
using Microsoft.Win32;

namespace DogeDebugger.UI.ViewModels.Panels;

public partial class MemorySearchViewModel : ObservableObject
{
    private const string AllModulesDisplay = "[全部模块]";

    private readonly DebuggerSession _session;
    private readonly Action<ulong> _navigateToAddress;
    private CancellationTokenSource? _scanCancellation;
    private MemoryValueMatch[]? _undoSnapshot;
    private readonly DispatcherTimer _savedAddressRefreshTimer;
    private bool _byteArrayOptionsSaved;
    private bool _savedIncludeMappedMemory;
    private bool? _savedWritable;
    private bool? _savedExecutable;
    private bool? _savedCopyOnWrite;

    [ObservableProperty]
    private ScanTypeOption _selectedScanType;

    [ObservableProperty]
    private ValueTypeOption _selectedValueType;

    [ObservableProperty]
    private string _searchValue = string.Empty;

    [ObservableProperty]
    private string _searchValueSecondary = string.Empty;

    [ObservableProperty]
    private bool _isHexValue;

    [ObservableProperty]
    private StringEncodingOption _selectedStringEncoding;

    [ObservableProperty]
    private int _customCodePage = 936;

    [ObservableProperty]
    private bool _includeAddressListStrings;

    [ObservableProperty]
    private bool _zeroTerminate;

    [ObservableProperty]
    private bool _ignoreCase;

    [ObservableProperty]
    private int _bitStart;

    [ObservableProperty]
    private int _bitLength;

    [ObservableProperty]
    private string _startAddress = "0";

    [ObservableProperty]
    private string _endAddress = "7FFFFFFFFFFF";

    [ObservableProperty]
    private bool _searchPrivateMemory = true;

    [ObservableProperty]
    private bool _searchImageMemory = true;

    [ObservableProperty]
    private bool _searchMappedMemory;

    [ObservableProperty]
    private bool? _isWritable = true;

    [ObservableProperty]
    private bool? _isExecutable;

    [ObservableProperty]
    private bool? _isCopyOnWrite;

    [ObservableProperty]
    private bool _includeMappedMemory;

    [ObservableProperty]
    private bool _isFastScanEnabled = true;

    [ObservableProperty]
    private int _fastScanAlignmentValue = 4;

    [ObservableProperty]
    private bool _isFastScanAligned = true;

    [ObservableProperty]
    private bool _isFastScanEnding;

    [ObservableProperty]
    private bool _pauseWhileScanning;

    [ObservableProperty]
    private string _selectedModule = AllModulesDisplay;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _hasSearched;

    [ObservableProperty]
    private bool _isNextScanAvailable;

    [ObservableProperty]
    private bool _isResultTruncated;

    [ObservableProperty]
    private long _resultCount;

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private string _title = "搜索 1";

    [ObservableProperty]
    private MemoryValueMatch? _selectedResult;

    [ObservableProperty]
    private object? _selectedSavedAddress;

    public MemorySearchViewModel(DebuggerSession session, Action<ulong> navigateToAddress)
    {
        _session = session;
        _navigateToAddress = navigateToAddress;
        _selectedScanType = ScanTypes[0];
        _selectedValueType = ValueTypes[2];
        _selectedStringEncoding = StringEncodingOptions[0];
        ModuleList.Add(AllModulesDisplay);
        _savedAddressRefreshTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(500),
            DispatcherPriority.Background,
            (_, _) => RefreshSavedAddressValues(),
            Dispatcher.CurrentDispatcher);
        _savedAddressRefreshTimer.Start();
    }

    public IReadOnlyList<ScanTypeOption> ScanTypes { get; } =
    [
        new("精确数值", MemoryScanType.ExactValue),
        new("大于...", MemoryScanType.BiggerThan),
        new("小于...", MemoryScanType.SmallerThan),
        new("介于...", MemoryScanType.ValueBetween),
        new("未知初始值", MemoryScanType.UnknownInitialValue),
        new("增大的值", MemoryScanType.IncreasedValue),
        new("减小的值", MemoryScanType.DecreasedValue),
        new("变化的值", MemoryScanType.ChangedValue),
        new("未变化的值", MemoryScanType.UnchangedValue),
        new("增大了...", MemoryScanType.IncreasedValueBy),
        new("减小了...", MemoryScanType.DecreasedValueBy)
    ];

    public IReadOnlyList<ValueTypeOption> ValueTypes { get; } =
    [
        new("单字节", MemoryValueKind.Byte),
        new("双字节", MemoryValueKind.Int16),
        new("四字节", MemoryValueKind.Int32),
        new("八字节", MemoryValueKind.Int64),
        new("浮点数", MemoryValueKind.Single),
        new("双精度浮点", MemoryValueKind.Double),
        new("字符串", MemoryValueKind.Utf8String),
        new("字节数组 (特征码/AOB)", MemoryValueKind.ByteArray),
        new("全部类型", MemoryValueKind.AllTypes)
    ];

    public IReadOnlyList<StringEncodingOption> StringEncodingOptions { get; } =
    [
        new("UTF-8", StringEncodingKind.Utf8),
        new("UTF-16 LE", StringEncodingKind.Utf16),
        new("UTF-16 BE", StringEncodingKind.Utf16BigEndian),
        new("UTF-32 LE", StringEncodingKind.Utf32),
        new("UTF-32 BE", StringEncodingKind.Utf32BigEndian),
        new("ASCII", StringEncodingKind.Ascii),
        new("GBK (简体中文)", StringEncodingKind.Gbk),
        new("GB18030", StringEncodingKind.Gb18030),
        new("BIG5 (繁体中文)", StringEncodingKind.Big5),
        new("Shift-JIS (日文)", StringEncodingKind.ShiftJis),
        new("EUC-KR (韩文)", StringEncodingKind.EucKr),
        new("Latin-1 (ISO-8859-1)", StringEncodingKind.Latin1),
        new("Windows-1252", StringEncodingKind.Windows1252),
        new("自定义代码页…", StringEncodingKind.Custom)
    ];

    public ObservableCollection<MemoryValueMatch> Results { get; } = [];

    public ObservableCollection<object> SavedAddresses { get; } = [];

    public ObservableCollection<string> ModuleList { get; } = [];

    public Visibility ProcessOverlayVisibility =>
        _session.Target.IsOpen ? Visibility.Collapsed : Visibility.Visible;

    public bool IsSearchValueEnabled => NeedsValue(SelectedScanType.Value);

    public bool IsSecondValueEnabled =>
        SelectedScanType.Value == MemoryScanType.ValueBetween;

    public bool IsStringType =>
        SelectedValueType.Value is
            MemoryValueKind.Utf8String or
            MemoryValueKind.Utf16String;

    public bool IsByteArrayType =>
        SelectedValueType.Value == MemoryValueKind.ByteArray;

    public bool IsFloatType =>
        SelectedValueType.Value is
            MemoryValueKind.Single or
            MemoryValueKind.Double;

    public bool IsMappedMemoryOptionVisible =>
        IsByteArrayType || IncludeMappedMemory;

    public bool IsCustomCodePage =>
        IsStringType &&
        SelectedStringEncoding.Value == StringEncodingKind.Custom;

    public bool IsCustomCodePageInvalid =>
        IsCustomCodePage &&
        !StringEncodingCatalog.IsValidCodePage(CustomCodePage);

    public string SearchValueHint => IsByteArrayType
        ? ByteArrayHint
        : IsStringType
            ? "输入要搜索的字符串"
            : "输入要搜索的数值";

    public string ByteArrayHint =>
        "通配符写 ??（独立分隔时可写 ?）；每 2 位为一个字节，至少要有一个固定字节。\n" +
        "可直接粘贴 IDA / x64dbg / CE / C 数组的写法：48 8B 05、488B05、48-8B-05、\n" +
        "\\x48\\x8B\\x05、0x48, 0x8B、{ 48 8B }，以及“字节串 + xx?? 掩码行”两行式。\n" +
        "支持多行粘贴。";

    public Visibility ByteArrayPlaceholderVisibility =>
        IsByteArrayType && string.IsNullOrEmpty(SearchValue)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public string SecondValue
    {
        get => SearchValueSecondary;
        set => SearchValueSecondary = value;
    }

    public int FastScanAlignment
    {
        get => FastScanAlignmentValue;
        set => FastScanAlignmentValue = value;
    }

    public bool WritableOnly
    {
        get => IsWritable == true;
        set => IsWritable = value ? true : null;
    }

    public bool ExecutableOnly
    {
        get => IsExecutable == true;
        set => IsExecutable = value ? true : null;
    }

    [RelayCommand(CanExecute = nameof(CanFirstScan))]
    private async Task FirstScanAsync()
    {
        if (!TryBuildOptions(out MemoryValueScanOptions? options, out string error))
        {
            StatusText = error;
            return;
        }

        _scanCancellation?.Dispose();
        _scanCancellation = new CancellationTokenSource();
        IsScanning = true;
        StatusText = "首次扫描中...";
        try
        {
            MemoryValueScanResult result = await Task.Run(
                    () => _session.ScanValues(options!, _scanCancellation.Token),
                    _scanCancellation.Token)
                .ConfigureAwait(true);
            _undoSnapshot = Results.ToArray();
            ApplyResult(result, firstScan: true);
        }
        catch (OperationCanceledException)
        {
            StatusText = "扫描已取消";
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanNextScan))]
    private async Task NextScanAsync()
    {
        if (!TryBuildOptions(out MemoryValueScanOptions? options, out string error))
        {
            StatusText = error;
            return;
        }

        MemoryValueMatch[] previous = Results.ToArray();
        _scanCancellation?.Dispose();
        _scanCancellation = new CancellationTokenSource();
        IsScanning = true;
        StatusText = "再次扫描中...";
        try
        {
            MemoryValueScanResult result = await Task.Run(
                    () => _session.RefineValues(previous, options!, _scanCancellation.Token),
                    _scanCancellation.Token)
                .ConfigureAwait(true);
            _undoSnapshot = previous;
            ApplyResult(result, firstScan: false);
        }
        catch (OperationCanceledException)
        {
            StatusText = "扫描已取消";
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelScan))]
    private void CancelScan()
    {
        _scanCancellation?.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanUndoScan))]
    private void UndoScan()
    {
        if (_undoSnapshot is null)
        {
            return;
        }

        Results.Clear();
        foreach (MemoryValueMatch match in _undoSnapshot)
        {
            Results.Add(match);
        }

        ResultCount = Results.Count;
        IsNextScanAvailable = Results.Count > 0;
        SelectedResult = Results.FirstOrDefault();
        IsResultTruncated = false;
        _undoSnapshot = null;
        StatusText = $"已撤销，恢复 {ResultCount:N0} 项";
    }

    [RelayCommand]
    private void ResetScan()
    {
        _scanCancellation?.Cancel();
        Results.Clear();
        _undoSnapshot = null;
        ResultCount = 0;
        HasSearched = false;
        IsNextScanAvailable = false;
        IsResultTruncated = false;
        SelectedResult = null;
        StatusText = "就绪";
    }

    [RelayCommand]
    private void AddSelectedToAddressList()
    {
        if (SelectedResult is null)
        {
            return;
        }

        MemoryValueKind kind = SelectedResult.Kind == MemoryValueKind.AllTypes
            ? MemoryValueKind.Int32
            : SelectedResult.Kind;
        if (SavedAddresses.OfType<SavedAddressRow>().Any(
                row => row.Address == SelectedResult.Address &&
                       row.ValueKind == kind))
        {
            StatusText = "该结果已存在于地址列表";
            return;
        }

        SavedAddresses.Add(new SavedAddressRow
        {
            Address = SelectedResult.Address,
            ValueKind = kind,
            Value = SelectedResult.DisplayValue,
            Description = string.Empty
        });
        StatusText = "已添加到地址列表";
    }

    [RelayCommand]
    private void AddAddressManually()
    {
        if (!TryParseAddress(StartAddress, out ulong address))
        {
            StatusText = "起始地址格式无效。";
            return;
        }

        MemoryValueKind kind = SelectedValueType.Value == MemoryValueKind.AllTypes
            ? MemoryValueKind.Int32
            : SelectedValueType.Value;
        SavedAddresses.Add(new SavedAddressRow
        {
            Address = address,
            ValueKind = kind,
            Description = string.Empty,
            Value = string.Empty
        });
        StatusText = "已添加手动地址";
    }

    [RelayCommand]
    private void ClearSavedAddresses()
    {
        SavedAddresses.Clear();
        StatusText = "已清空地址列表";
    }

    [RelayCommand]
    private void OpenSelectedSavedAddress()
    {
        if (SelectedSavedAddress is SavedAddressRow row)
        {
            _navigateToAddress(row.Address);
        }
    }

    [RelayCommand]
    private void ToggleSelectedSavedAddressFrozen()
    {
        if (SelectedSavedAddress is SavedAddressRow row)
        {
            row.IsFrozen = !row.IsFrozen;
            StatusText = row.IsFrozen ? "已冻结地址" : "已解冻地址";
        }
    }

    [RelayCommand]
    private void DeleteSelectedSavedAddress()
    {
        if (SelectedSavedAddress is not null)
        {
            SavedAddresses.Remove(SelectedSavedAddress);
            SelectedSavedAddress = null;
            StatusText = "已删除地址";
        }
    }

    [RelayCommand]
    private void CopySelectedSavedAddress()
    {
        if (SelectedSavedAddress is SavedAddressRow row)
        {
            Clipboard.SetText(row.AddressText);
            StatusText = "已复制地址";
        }
    }

    [RelayCommand]
    private void ImportAddressList()
    {
        OpenFileDialog dialog = new()
        {
            Title = "导入地址列表",
            Filter = "Cheat Engine 表格 (*.CT;*.ct;*.xml)|*.CT;*.ct;*.xml|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            IReadOnlyList<SavedAddressRowSource> rows =
                SavedAddressTableCodec.Deserialize(
                    File.ReadAllText(dialog.FileName),
                    _session.Target.IsOpen ? _session.EnumerateModules() : []);
            foreach (SavedAddressRowSource source in rows)
            {
                SavedAddresses.Add(new SavedAddressRow
                {
                    Address = source.Address,
                    Description = source.Description,
                    ValueKind = source.ValueKind,
                    Value = source.Value,
                    IsHexadecimal = source.IsHexadecimal,
                    IsSigned = source.IsSigned
                });
            }

            StatusText = $"已导入 {rows.Count:N0} 个地址";
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }
    }

    [RelayCommand]
    private void ExportAddressList()
    {
        if (SavedAddresses.OfType<SavedAddressRow>().Any() == false)
        {
            StatusText = "地址列表为空";
            return;
        }

        SaveFileDialog dialog = new()
        {
            Title = "导出地址列表",
            Filter = "Cheat Engine 表格 (*.CT)|*.CT|XML 文件 (*.xml)|*.xml",
            DefaultExt = ".CT",
            AddExtension = true,
            FileName = "DogeDebugger.CT"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            string xml = SavedAddressTableCodec.Serialize(
                SavedAddresses
                    .OfType<SavedAddressRow>()
                    .Select(static row => new SavedAddressRowSource
                    {
                        Address = row.Address,
                        Description = row.Description,
                        ValueKind = row.ValueKind,
                        Value = row.Value,
                        IsHexadecimal = row.IsHexadecimal,
                        IsSigned = row.IsSigned
                    }));
            File.WriteAllText(dialog.FileName, xml, new UTF8Encoding(false));
            StatusText = $"已导出 {SavedAddresses.OfType<SavedAddressRow>().Count():N0} 个地址";
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }
    }

    [RelayCommand]
    private void CopySelectedAddress()
    {
        if (SelectedResult is not null)
        {
            Clipboard.SetText(SelectedResult.AddressText);
            StatusText = "已复制地址";
        }
    }

    [RelayCommand]
    private void OpenSelectedResult()
    {
        if (SelectedResult is not null)
        {
            _navigateToAddress(SelectedResult.Address);
        }
    }

    public void RefreshTargetState()
    {
        OnPropertyChanged(nameof(ProcessOverlayVisibility));
        RefreshModuleList();
        FirstScanCommand.NotifyCanExecuteChanged();
        NextScanCommand.NotifyCanExecuteChanged();
    }

    public void CopyOptionsFrom(MemorySearchViewModel source)
    {
        SelectedScanType = source.SelectedScanType;
        SelectedValueType = source.SelectedValueType;
        SearchValue = source.SearchValue;
        SearchValueSecondary = source.SearchValueSecondary;
        IsHexValue = source.IsHexValue;
        SelectedStringEncoding = source.SelectedStringEncoding;
        CustomCodePage = source.CustomCodePage;
        IncludeAddressListStrings = source.IncludeAddressListStrings;
        ZeroTerminate = source.ZeroTerminate;
        IgnoreCase = source.IgnoreCase;
        BitStart = source.BitStart;
        BitLength = source.BitLength;
        StartAddress = source.StartAddress;
        EndAddress = source.EndAddress;
        SearchPrivateMemory = source.SearchPrivateMemory;
        SearchImageMemory = source.SearchImageMemory;
        SearchMappedMemory = source.SearchMappedMemory;
        IsWritable = source.IsWritable;
        IsExecutable = source.IsExecutable;
        IsCopyOnWrite = source.IsCopyOnWrite;
        IncludeMappedMemory = source.IncludeMappedMemory;
        IsFastScanEnabled = source.IsFastScanEnabled;
        FastScanAlignmentValue = source.FastScanAlignmentValue;
        IsFastScanEnding = source.IsFastScanEnding;
        IsFastScanAligned = source.IsFastScanAligned;
        PauseWhileScanning = source.PauseWhileScanning;
        SelectedModule = source.SelectedModule;
    }

    public string? ReadValuePreview(ulong address, MemoryValueKind kind)
    {
        if (!_session.Target.IsOpen)
        {
            return null;
        }

        int size = MemoryValueSample.SizeOf(kind);
        if (size <= 0)
        {
            return null;
        }

        try
        {
            byte[] bytes = _session.ReadBytes(address, size);
            return MemoryValueSample.TryCreate(
                    kind,
                    bytes,
                    ignoreCase: false,
                    out MemoryValueSample? sample) &&
                sample is not null
                ? sample.ToDisplayString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    public void AddAddressFromSource(SavedAddressRowSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        SavedAddresses.Add(new SavedAddressRow
        {
            Address = source.Address,
            Description = source.Description,
            ValueKind = source.ValueKind,
            Value = source.Value,
            IsHexadecimal = source.IsHexadecimal,
            IsSigned = source.IsSigned
        });
        StatusText = "已添加地址";
    }

    private bool CanFirstScan()
    {
        return _session.Target.IsOpen &&
               !IsScanning &&
               IsByteArraySearchInputValid;
    }

    private bool CanNextScan()
    {
        return _session.Target.IsOpen &&
               !IsScanning &&
               IsNextScanAvailable &&
               IsByteArraySearchInputValid;
    }

    private bool CanCancelScan()
    {
        return IsScanning;
    }

    private bool CanUndoScan()
    {
        return !IsScanning && _undoSnapshot is not null;
    }

    private bool IsByteArraySearchInputValid =>
        !IsByteArrayType ||
        (!string.IsNullOrWhiteSpace(SearchValue) &&
         BytePattern.TryParse(SearchValue, out _, out _));

    private void RefreshModuleList()
    {
        string selected = SelectedModule;
        ModuleList.Clear();
        ModuleList.Add(AllModulesDisplay);

        if (_session.Target.IsOpen)
        {
            try
            {
                foreach (ModuleDescriptor module in _session.EnumerateModules())
                {
                    if (!string.IsNullOrWhiteSpace(module.Name) &&
                        !ModuleList.Contains(module.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        ModuleList.Add(module.Name);
                    }
                }
            }
            catch
            {
                // The target can close between the IsOpen check and module enumeration.
            }
        }

        SelectedModule = ModuleList.Contains(selected, StringComparer.OrdinalIgnoreCase)
            ? selected
            : AllModulesDisplay;
    }

    private void RefreshSavedAddressValues()
    {
        if (!_session.Target.IsOpen)
        {
            return;
        }

        foreach (SavedAddressRow row in SavedAddresses.OfType<SavedAddressRow>())
        {
            if (row.IsFrozen || row.ValueKind == MemoryValueKind.AllTypes)
            {
                continue;
            }

            int size = MemoryValueSample.SizeOf(row.ValueKind);
            if (size <= 0)
            {
                continue;
            }

            try
            {
                byte[] bytes = _session.ReadBytes(row.Address, size);
                if (MemoryValueSample.TryCreate(
                        row.ValueKind,
                        bytes,
                        ignoreCase: false,
                        out MemoryValueSample? sample) &&
                    sample is not null)
                {
                    row.Value = sample.ToDisplayString();
                }
            }
            catch
            {
                // The target can close or change protection while the timer runs.
            }
        }
    }

    private bool TryBuildOptions(
        out MemoryValueScanOptions? options,
        out string error)
    {
        options = null;
        error = string.Empty;

        if (!TryParseAddress(StartAddress, out ulong startAddress))
        {
            error = "起始地址格式无效。";
            return false;
        }

        if (!TryParseAddress(EndAddress, out ulong endAddress))
        {
            error = "结束地址格式无效。";
            return false;
        }

        if (endAddress < startAddress)
        {
            error = "结束地址不能小于起始地址。";
            return false;
        }

        if (IsFastScanEnabled &&
            FastScanAlignmentValue is not (1 or 2 or 4 or 8 or 16))
        {
            error = "快速扫描对齐值必须为 1、2、4、8 或 16。";
            return false;
        }

        if (IsCustomCodePageInvalid)
        {
            error = "代码页无效。";
            return false;
        }

        if (BitStart < 0 || BitLength < 0 || BitStart + BitLength > 64)
        {
            error = "位起始和位长度必须在 0 到 64 位范围内。";
            return false;
        }

        MemoryScanType scanType = SelectedScanType.Value;
        if (NeedsValue(scanType) && string.IsNullOrWhiteSpace(SearchValue))
        {
            error = "请输入扫描数值。";
            return false;
        }

        if (scanType == MemoryScanType.ValueBetween &&
            string.IsNullOrWhiteSpace(SearchValueSecondary))
        {
            error = "请输入区间结束值。";
            return false;
        }

        if (IsByteArrayType &&
            !BytePattern.TryParse(
                SearchValue,
                out _,
                out error))
        {
            return false;
        }

        Encoding textEncoding = IsStringType
            ? StringEncodingCatalog.Resolve(
                SelectedStringEncoding.Value,
                CustomCodePage)
            : Encoding.UTF8;

        options = new MemoryValueScanOptions
        {
            Kind = SelectedValueType.Value,
            Comparison = ToComparison(scanType),
            Value = PrepareValue(SearchValue),
            SecondValue = PrepareValue(SearchValueSecondary),
            ModuleName = SelectedModule == AllModulesDisplay
                ? null
                : SelectedModule,
            StartAddress = startAddress,
            EndAddress = endAddress,
            Alignment = IsFastScanEnabled ? FastScanAlignmentValue : 1,
            SearchPrivateMemory = SearchPrivateMemory,
            SearchImageMemory = SearchImageMemory,
            SearchMappedMemory = IncludeMappedMemory,
            WritableOnly = false,
            ExecutableOnly = false,
            RequireWritable = IsWritable,
            RequireExecutable = IsExecutable,
            RequireCopyOnWrite = IsCopyOnWrite,
            PauseWhileScanning = PauseWhileScanning,
            IgnoreCase = IsStringType && IgnoreCase,
            TextEncoding = textEncoding,
            IncludeAddressListStrings = IncludeAddressListStrings,
            ZeroTerminate = ZeroTerminate,
            BitStart = BitStart,
            BitLength = BitLength
        };
        return true;
    }

    private string PrepareValue(string value)
    {
        string normalized = NormalizeValue(value);
        if (!IsHexValue ||
            IsStringType ||
            IsByteArrayType ||
            string.IsNullOrWhiteSpace(normalized) ||
            normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        return "0x" + normalized;
    }

    private void ApplyResult(MemoryValueScanResult result, bool firstScan)
    {
        Results.Clear();
        IReadOnlyList<ModuleDescriptor> modules = _session.Target.IsOpen
            ? _session.EnumerateModules()
            : [];
        foreach (MemoryValueMatch match in result.Matches)
        {
            match.DisplayAddress = FormatDisplayAddress(match.Address, modules);
            Results.Add(match);
        }

        ResultCount = result.Matches.Count;
        HasSearched = true;
        IsNextScanAvailable = Results.Count > 0;
        IsResultTruncated = result.Truncated;
        SelectedResult = Results.FirstOrDefault();
        StatusText = result.Truncated
            ? $"已达到结果上限，显示 {ResultCount:N0} 项"
            : firstScan
                ? $"首次扫描完成，共 {ResultCount:N0} 项"
                : $"再次扫描完成，剩余 {ResultCount:N0} 项";
    }

    private static string FormatDisplayAddress(
        ulong address,
        IReadOnlyList<ModuleDescriptor> modules)
    {
        foreach (ModuleDescriptor module in modules)
        {
            if (address >= module.BaseAddress &&
                address < module.BaseAddress + module.Size)
            {
                return $"{module.Name}+{address - module.BaseAddress:X}";
            }
        }

        return $"0x{address:X}";
    }

    private static string NormalizeValue(string value)
    {
        return value.Trim();
    }

    private static bool NeedsValue(MemoryScanType scanType)
    {
        return scanType is not (
            MemoryScanType.UnknownInitialValue or
            MemoryScanType.IncreasedValue or
            MemoryScanType.DecreasedValue or
            MemoryScanType.ChangedValue or
            MemoryScanType.UnchangedValue);
    }

    private static MemoryValueComparison ToComparison(MemoryScanType scanType)
    {
        return scanType switch
        {
            MemoryScanType.ExactValue => MemoryValueComparison.Exact,
            MemoryScanType.BiggerThan => MemoryValueComparison.GreaterThan,
            MemoryScanType.SmallerThan => MemoryValueComparison.LessThan,
            MemoryScanType.ValueBetween => MemoryValueComparison.Between,
            MemoryScanType.UnknownInitialValue => MemoryValueComparison.UnknownInitialValue,
            MemoryScanType.IncreasedValue => MemoryValueComparison.Increased,
            MemoryScanType.DecreasedValue => MemoryValueComparison.Decreased,
            MemoryScanType.ChangedValue => MemoryValueComparison.Changed,
            MemoryScanType.UnchangedValue => MemoryValueComparison.Unchanged,
            MemoryScanType.IncreasedValueBy => MemoryValueComparison.IncreasedBy,
            MemoryScanType.DecreasedValueBy => MemoryValueComparison.DecreasedBy,
            _ => throw new ArgumentOutOfRangeException(nameof(scanType))
        };
    }

    private static bool TryParseAddress(string text, out ulong address)
    {
        string value = text.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
            return ulong.TryParse(
                value,
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out address);
        }

        return ulong.TryParse(
                   value,
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out address) ||
               ulong.TryParse(
                   value,
                   NumberStyles.AllowHexSpecifier,
                   CultureInfo.InvariantCulture,
                   out address);
    }

    partial void OnSelectedScanTypeChanged(ScanTypeOption value)
    {
        OnPropertyChanged(nameof(IsSearchValueEnabled));
        OnPropertyChanged(nameof(IsSecondValueEnabled));
    }

    partial void OnSelectedValueTypeChanged(ValueTypeOption value)
    {
        if (value.Value == MemoryValueKind.ByteArray)
        {
            if (!_byteArrayOptionsSaved)
            {
                _savedIncludeMappedMemory = IncludeMappedMemory;
                _savedWritable = IsWritable;
                _savedExecutable = IsExecutable;
                _savedCopyOnWrite = IsCopyOnWrite;
                _byteArrayOptionsSaved = true;
                IncludeMappedMemory = true;
                IsWritable = false;
                IsExecutable = true;
                IsCopyOnWrite = null;
            }
        }
        else if (_byteArrayOptionsSaved)
        {
            IncludeMappedMemory = _savedIncludeMappedMemory;
            IsWritable = _savedWritable;
            IsExecutable = _savedExecutable;
            IsCopyOnWrite = _savedCopyOnWrite;
            _byteArrayOptionsSaved = false;
        }

        OnPropertyChanged(nameof(IsStringType));
        OnPropertyChanged(nameof(IsByteArrayType));
        OnPropertyChanged(nameof(IsFloatType));
        OnPropertyChanged(nameof(IsCustomCodePage));
        OnPropertyChanged(nameof(IsCustomCodePageInvalid));
        OnPropertyChanged(nameof(IsMappedMemoryOptionVisible));
        OnPropertyChanged(nameof(SearchValueHint));
        OnPropertyChanged(nameof(ByteArrayHint));
        OnPropertyChanged(nameof(ByteArrayPlaceholderVisibility));
        FirstScanCommand.NotifyCanExecuteChanged();
        NextScanCommand.NotifyCanExecuteChanged();
    }

    partial void OnSearchValueChanged(string value)
    {
        OnPropertyChanged(nameof(ByteArrayPlaceholderVisibility));
        FirstScanCommand.NotifyCanExecuteChanged();
        NextScanCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedStringEncodingChanged(StringEncodingOption value)
    {
        OnPropertyChanged(nameof(IsCustomCodePage));
        OnPropertyChanged(nameof(IsCustomCodePageInvalid));
    }

    partial void OnSelectedModuleChanged(string value)
    {
        OnPropertyChanged(nameof(IsMappedMemoryOptionVisible));
    }

    partial void OnCustomCodePageChanged(int value) =>
        OnPropertyChanged(nameof(IsCustomCodePageInvalid));

    partial void OnIncludeMappedMemoryChanged(bool value) =>
        OnPropertyChanged(nameof(IsMappedMemoryOptionVisible));

    partial void OnIsScanningChanged(bool value)
    {
        FirstScanCommand.NotifyCanExecuteChanged();
        NextScanCommand.NotifyCanExecuteChanged();
        CancelScanCommand.NotifyCanExecuteChanged();
        UndoScanCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsNextScanAvailableChanged(bool value)
    {
        NextScanCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsFastScanAlignedChanged(bool value)
    {
        if (value)
        {
            IsFastScanEnding = false;
        }
    }

    partial void OnIsFastScanEndingChanged(bool value)
    {
        if (value)
        {
            IsFastScanAligned = false;
        }
    }
}
