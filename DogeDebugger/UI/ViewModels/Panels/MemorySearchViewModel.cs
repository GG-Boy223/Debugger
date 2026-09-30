using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DogeDebugger.Core.Search;
using DogeDebugger.Debugger.Session;

namespace DogeDebugger.UI.ViewModels.Panels;

public partial class MemorySearchViewModel : ObservableObject
{
    private readonly DebuggerSession _session;
    private readonly Action<ulong> _navigateToAddress;
    private CancellationTokenSource? _scanCancellation;

    [ObservableProperty]
    private ScanTypeOption _selectedScanType;

    [ObservableProperty]
    private ValueTypeOption _selectedValueType;

    [ObservableProperty]
    private string _searchValue = string.Empty;

    [ObservableProperty]
    private string _secondValue = string.Empty;

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
    private string _endAddress = "7FFFFFFFFFFFFFFF";

    [ObservableProperty]
    private bool _searchPrivateMemory = true;

    [ObservableProperty]
    private bool _searchImageMemory = true;

    [ObservableProperty]
    private bool _searchMappedMemory;

    [ObservableProperty]
    private bool _writableOnly;

    [ObservableProperty]
    private bool _executableOnly;

    [ObservableProperty]
    private bool _fastScanEnabled = true;

    [ObservableProperty]
    private int _fastScanAlignment = 4;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _hasSearched;

    [ObservableProperty]
    private bool _isNextScanAvailable;

    [ObservableProperty]
    private long _resultCount;

    [ObservableProperty]
    private string _statusText = "等待首次扫描";

    [ObservableProperty]
    private string _title = "搜索 1";

    [ObservableProperty]
    private MemoryValueMatch? _selectedResult;

    public MemorySearchViewModel(DebuggerSession session, Action<ulong> navigateToAddress)
    {
        _session = session;
        _navigateToAddress = navigateToAddress;
        _selectedScanType = ScanTypes[0];
        _selectedValueType = ValueTypes[0];
        _selectedStringEncoding = StringEncodingOptions[0];
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
        new("Byte", MemoryValueKind.Byte),
        new("SByte", MemoryValueKind.SByte),
        new("2 Bytes", MemoryValueKind.Int16),
        new("2 Bytes unsigned", MemoryValueKind.UInt16),
        new("4 Bytes", MemoryValueKind.Int32),
        new("4 Bytes unsigned", MemoryValueKind.UInt32),
        new("8 Bytes", MemoryValueKind.Int64),
        new("8 Bytes unsigned", MemoryValueKind.UInt64),
        new("Float", MemoryValueKind.Single),
        new("Double", MemoryValueKind.Double),
        new("UTF-8", MemoryValueKind.Utf8String),
        new("UTF-16", MemoryValueKind.Utf16String),
        new("Array of Bytes", MemoryValueKind.ByteArray)
    ];

    public IReadOnlyList<StringEncodingOption> StringEncodingOptions { get; } =
    [
        new("UTF-8", StringEncodingKind.Utf8),
        new("GBK", StringEncodingKind.Gbk),
        new("ASCII", StringEncodingKind.Ascii),
        new("UTF-16", StringEncodingKind.Utf16),
        new("BIG5", StringEncodingKind.Big5),
        new("UTF-16 BE", StringEncodingKind.Utf16BigEndian),
        new("UTF-32", StringEncodingKind.Utf32),
        new("UTF-32 BE", StringEncodingKind.Utf32BigEndian),
        new("Shift-JIS", StringEncodingKind.ShiftJis),
        new("EUC-KR", StringEncodingKind.EucKr),
        new("GB18030", StringEncodingKind.Gb18030),
        new("Latin-1", StringEncodingKind.Latin1),
        new("Windows-1252", StringEncodingKind.Windows1252),
        new("自定义代码页", StringEncodingKind.Custom)
    ];

    public ObservableCollection<MemoryValueMatch> Results { get; } = [];

    public ObservableCollection<object> SavedAddresses { get; } = [];

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

    public bool IsCustomCodePage =>
        IsStringType &&
        SelectedStringEncoding.Value == StringEncodingKind.Custom;

    public bool IsCustomCodePageInvalid =>
        IsCustomCodePage &&
        !StringEncodingCatalog.IsValidCodePage(CustomCodePage);

    public string SearchValueHint => IsByteArrayType
        ? "AOB 模式，例如 48 8B ?? 89 5C 24 08"
        : IsStringType
            ? "输入要搜索的字符串"
            : "输入要搜索的数值";

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

    [RelayCommand]
    private void Reset()
    {
        _scanCancellation?.Cancel();
        Results.Clear();
        ResultCount = 0;
        HasSearched = false;
        IsNextScanAvailable = false;
        SelectedResult = null;
        StatusText = "等待首次扫描";
    }

    public void RefreshTargetState()
    {
        OnPropertyChanged(nameof(ProcessOverlayVisibility));
    }

    public void CopyOptionsFrom(MemorySearchViewModel source)
    {
        SelectedScanType = source.SelectedScanType;
        SelectedValueType = source.SelectedValueType;
        SearchValue = source.SearchValue;
        SecondValue = source.SecondValue;
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
        WritableOnly = source.WritableOnly;
        ExecutableOnly = source.ExecutableOnly;
        FastScanEnabled = source.FastScanEnabled;
        FastScanAlignment = source.FastScanAlignment;
    }

    [RelayCommand]
    private void OpenSelectedResult()
    {
        if (SelectedResult is not null)
        {
            _navigateToAddress(SelectedResult.Address);
        }
    }

    private bool CanFirstScan()
    {
        return !IsScanning;
    }

    private bool CanNextScan()
    {
        return !IsScanning && IsNextScanAvailable;
    }

    private bool CanCancelScan()
    {
        return IsScanning;
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

        if (FastScanEnabled &&
            FastScanAlignment is not (1 or 2 or 4 or 8 or 16))
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
            string.IsNullOrWhiteSpace(SecondValue))
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
            Value = NormalizeValue(SearchValue),
            SecondValue = NormalizeValue(SecondValue),
            StartAddress = startAddress,
            EndAddress = endAddress,
            Alignment = FastScanEnabled ? FastScanAlignment : 1,
            SearchPrivateMemory = SearchPrivateMemory,
            SearchImageMemory = SearchImageMemory,
            SearchMappedMemory = SearchMappedMemory,
            WritableOnly = WritableOnly,
            ExecutableOnly = ExecutableOnly,
            IgnoreCase = IsStringType && IgnoreCase,
            TextEncoding = textEncoding,
            IncludeAddressListStrings = IncludeAddressListStrings,
            ZeroTerminate = ZeroTerminate,
            BitStart = BitStart,
            BitLength = BitLength
        };
        return true;
    }

    private void ApplyResult(MemoryValueScanResult result, bool firstScan)
    {
        Results.Clear();
        foreach (MemoryValueMatch match in result.Matches)
        {
            Results.Add(match);
        }

        ResultCount = result.Matches.Count;
        HasSearched = true;
        IsNextScanAvailable = Results.Count > 0;
        SelectedResult = Results.FirstOrDefault();
        StatusText = result.Truncated
            ? $"已达到结果上限，显示 {ResultCount:N0} 项"
            : firstScan
                ? $"首次扫描完成，共 {ResultCount:N0} 项"
                : $"再次扫描完成，剩余 {ResultCount:N0} 项";
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
        OnPropertyChanged(nameof(IsStringType));
        OnPropertyChanged(nameof(IsByteArrayType));
        OnPropertyChanged(nameof(IsCustomCodePage));
        OnPropertyChanged(nameof(IsCustomCodePageInvalid));
        OnPropertyChanged(nameof(SearchValueHint));
    }

    partial void OnSelectedStringEncodingChanged(StringEncodingOption value)
    {
        OnPropertyChanged(nameof(IsCustomCodePage));
        OnPropertyChanged(nameof(IsCustomCodePageInvalid));
    }

    partial void OnCustomCodePageChanged(int value) =>
        OnPropertyChanged(nameof(IsCustomCodePageInvalid));

    partial void OnIsScanningChanged(bool value)
    {
        FirstScanCommand.NotifyCanExecuteChanged();
        NextScanCommand.NotifyCanExecuteChanged();
        CancelScanCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsNextScanAvailableChanged(bool value)
    {
        NextScanCommand.NotifyCanExecuteChanged();
    }
}
