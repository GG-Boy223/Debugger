using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DogeDebugger.Core.Disassembly;
using DogeDebugger.Debugger.Session;
using DogeDebugger.Debugger.UserMode;

namespace DogeDebugger.UI.ViewModels.Dialogs;

public partial class InstructionAccessWatchViewModel : ObservableObject, IDisposable
{
    private readonly InstructionAccessWatch _watch;
    private readonly Dictionary<ulong, InstructionAccessDisplayItem> _records = [];
    private bool _disposed;

    public InstructionAccessWatchViewModel(
        DebuggerSession session,
        InstructionSnapshot instruction,
        InstructionMemoryOperand operand,
        int bitness)
    {
        _watch = new InstructionAccessWatch(
            session,
            instruction,
            operand,
            bitness);
        _watch.Changed += OnWatchChanged;
        _watch.RecordsUpdated += OnRecordsUpdated;
        CodeAddressText = $"{instruction.Address:X}";
        Title = $"指令访问的地址 - {instruction.Address:X}";
        ValueSizeOptions =
        [
            new InstructionAccessValueSizeOption(1, "1 字节"),
            new InstructionAccessValueSizeOption(2, "2 字节"),
            new InstructionAccessValueSizeOption(4, "4 字节"),
            new InstructionAccessValueSizeOption(8, "8 字节")
        ];
        _selectedValueSize = ValueSizeOptions.FirstOrDefault(
                option => option.ValueSize == operand.DefaultValueSize)
            ?? ValueSizeOptions[0];
    }

    public event Action<string>? ShowError;

    public event Action? CloseRequested;

    public event Action<ulong>? NavigateToHexRequested;

    public event Action<ulong>? OpenRttiRequested;

    public event Action<InstructionAccessDisplayItem>? ParseMonoStructRequested;

    public ObservableCollection<InstructionAccessDisplayItem> Records { get; } = [];

    public ObservableCollection<InstructionAccessValueSizeOption> ValueSizeOptions { get; }

    public Func<bool>? CanParseMonoStructProvider { get; set; }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _codeAddressText;

    [ObservableProperty]
    private string _summaryText = "以下 0 个地址被所选代码访问";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleActionCommand))]
    private bool _isActive;

    [ObservableProperty]
    private string _actionButtonText = "停止";

    [ObservableProperty]
    private bool _isValueHexDisplay = true;

    [ObservableProperty]
    private InstructionAccessValueSizeOption _selectedValueSize;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NavigateSelectedAddressInHexViewCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedAddressInRttiCommand))]
    [NotifyCanExecuteChangedFor(nameof(ParseSelectedMonoStructCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopySelectedAddressCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopySelectedValueCommand))]
    private InstructionAccessDisplayItem? _selectedRecord;

    public string ValueFormatToggleMenuHeader =>
        IsValueHexDisplay ? "切换为 10 进制显示" : "切换为 16 进制显示";

    public bool ShowParseSelectedMonoStructMenu =>
        SelectedRecord?.CanParseMonoStruct == true &&
        CanParseMonoStructProvider?.Invoke() == true;

    public void RefreshContextCommandState()
    {
        OnPropertyChanged(nameof(ShowParseSelectedMonoStructMenu));
        ParseSelectedMonoStructCommand.NotifyCanExecuteChanged();
    }

    public bool Start()
    {
        if (_watch.Start(out string error))
        {
            IsActive = true;
            ActionButtonText = "停止";
            return true;
        }

        ShowError?.Invoke(error);
        return false;
    }

    public void Stop()
    {
        if (!_watch.IsActive)
        {
            return;
        }

        _watch.Stop();
        IsActive = false;
        ActionButtonText = "关闭";
        UpdateSummary();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _watch.Changed -= OnWatchChanged;
        _watch.RecordsUpdated -= OnRecordsUpdated;
        _watch.Dispose();
        _disposed = true;
    }

    [RelayCommand]
    private void ToggleAction()
    {
        if (IsActive)
        {
            Stop();
        }
        else
        {
            CloseRequested?.Invoke();
        }
    }

    [RelayCommand]
    private void ToggleValueNumberBase()
    {
        IsValueHexDisplay = !IsValueHexDisplay;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedRecord))]
    private void NavigateSelectedAddressInHexView()
    {
        if (SelectedRecord is not null)
        {
            NavigateToHexRequested?.Invoke(SelectedRecord.Address);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedRecord))]
    private void OpenSelectedAddressInRtti()
    {
        if (SelectedRecord is not null)
        {
            OpenRttiRequested?.Invoke(SelectedRecord.Address);
        }
    }

    [RelayCommand(CanExecute = nameof(CanParseSelectedMonoStruct))]
    private void ParseSelectedMonoStruct()
    {
        if (SelectedRecord is not null &&
            SelectedRecord.CanParseMonoStruct)
        {
            ParseMonoStructRequested?.Invoke(SelectedRecord);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedRecord))]
    private void CopySelectedAddress()
    {
        if (SelectedRecord is not null)
        {
            SetClipboard(SelectedRecord.AddressText);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedRecord))]
    private void CopySelectedValue()
    {
        if (SelectedRecord is not null)
        {
            SetClipboard(SelectedRecord.ValueText);
        }
    }

    partial void OnSelectedValueSizeChanged(
        InstructionAccessValueSizeOption value)
    {
        if (_disposed || value is null)
        {
            return;
        }

        _records.Clear();
        Records.Clear();
        SelectedRecord = null;
        UpdateSummary();
        _watch.ValueSize = value.ValueSize;
        OnPropertyChanged(nameof(ShowParseSelectedMonoStructMenu));
    }

    partial void OnIsValueHexDisplayChanged(bool value)
    {
        foreach (InstructionAccessDisplayItem record in Records)
        {
            record.RefreshValueText(value);
        }

        OnPropertyChanged(nameof(ValueFormatToggleMenuHeader));
    }

    partial void OnSelectedRecordChanged(
        InstructionAccessDisplayItem? value)
    {
        OnPropertyChanged(nameof(ShowParseSelectedMonoStructMenu));
    }

    private void OnRecordsUpdated(
        IReadOnlyList<InstructionAccessWatchRecord> records)
    {
        Dispatcher dispatcher =
            Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => ApplyRecords(records));
            return;
        }

        ApplyRecords(records);
    }

    private void ApplyRecords(
        IReadOnlyList<InstructionAccessWatchRecord> records)
    {
        ulong? selectedAddress = SelectedRecord?.Address;
        HashSet<ulong> incoming = records
            .Select(static record => record.AccessAddress)
            .ToHashSet();

        foreach (InstructionAccessWatchRecord record in records)
        {
            if (!_records.TryGetValue(
                    record.AccessAddress,
                    out InstructionAccessDisplayItem? item))
            {
                item = new InstructionAccessDisplayItem(record.AccessAddress);
                _records.Add(record.AccessAddress, item);
                Records.Add(item);
            }

            item.Update(record, IsValueHexDisplay);
        }

        foreach (InstructionAccessDisplayItem removed in Records
                     .Where(item => !incoming.Contains(item.Address))
                     .ToArray())
        {
            _records.Remove(removed.Address);
            Records.Remove(removed);
        }

        if (selectedAddress is { } address &&
            _records.TryGetValue(address, out InstructionAccessDisplayItem? selected))
        {
            SelectedRecord = selected;
        }
        else if (SelectedRecord is null)
        {
            SelectedRecord = Records.FirstOrDefault();
        }

        UpdateSummary();
    }

    private void OnWatchChanged()
    {
        Dispatcher dispatcher =
            Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() =>
            {
                if (!_watch.IsActive)
                {
                    IsActive = false;
                    ActionButtonText = "关闭";
                }
            });
            return;
        }

        if (!_watch.IsActive)
        {
            IsActive = false;
            ActionButtonText = "关闭";
        }
    }

    private bool HasSelectedRecord() => SelectedRecord is not null;

    private bool CanParseSelectedMonoStruct() =>
        ShowParseSelectedMonoStructMenu;

    private void UpdateSummary() =>
        SummaryText = $"以下 {Records.Count} 个地址被所选代码访问";

    private static void SetClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
        }
    }
}

public sealed class InstructionAccessValueSizeOption
{
    public InstructionAccessValueSizeOption(int valueSize, string displayText)
    {
        ValueSize = valueSize;
        DisplayText = displayText;
    }

    public int ValueSize { get; }

    public string DisplayText { get; }

    public override string ToString() => DisplayText;
}

public sealed class InstructionAccessDisplayItem : ObservableObject
{
    private ulong _value;
    private int _valueSize = 8;
    private int _count;
    private string _valueText = "无法读取";
    private bool _valueReadSucceeded;
    private string? _monoBaseRegisterName;
    private ulong _monoBaseAddress;
    private uint _lastThreadId;
    private DateTime _firstSeen;
    private DateTime _lastSeen;

    public InstructionAccessDisplayItem(ulong address)
    {
        Address = address;
        AddressText = $"{address:X}";
    }

    public ulong Address { get; }

    public string AddressText { get; }

    public int Count
    {
        get => _count;
        private set => SetProperty(ref _count, value);
    }

    public string ValueText
    {
        get => _valueText;
        private set => SetProperty(ref _valueText, value);
    }

    public bool CanParseMonoStruct =>
        !string.IsNullOrWhiteSpace(_monoBaseRegisterName) &&
        _monoBaseAddress > 0;

    public string? MonoBaseRegisterName => _monoBaseRegisterName;

    public ulong MonoBaseAddress => _monoBaseAddress;

    public uint LastThreadId => _lastThreadId;

    public DateTime FirstSeen => _firstSeen;

    public DateTime LastSeen => _lastSeen;

    public void Update(
        InstructionAccessWatchRecord record,
        bool hexadecimal)
    {
        _value = record.Value;
        _valueReadSucceeded = record.ValueReadSucceeded;
        _valueSize = record.ValueSize;
        _count = record.Count;
        _monoBaseRegisterName = record.MonoBaseRegisterName;
        _monoBaseAddress = record.MonoBaseAddress;
        _lastThreadId = record.LastThreadId;
        _firstSeen = record.FirstSeen;
        _lastSeen = record.LastSeen;
        RefreshValueText(hexadecimal);
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(CanParseMonoStruct));
        OnPropertyChanged(nameof(MonoBaseRegisterName));
        OnPropertyChanged(nameof(MonoBaseAddress));
        OnPropertyChanged(nameof(LastThreadId));
        OnPropertyChanged(nameof(FirstSeen));
        OnPropertyChanged(nameof(LastSeen));
    }

    public void RefreshValueText(bool hexadecimal)
    {
        if (!_valueReadSucceeded)
        {
            ValueText = "无法读取";
            return;
        }

        if (!hexadecimal)
        {
            ValueText = _value.ToString();
            return;
        }

        int width = _valueSize * 2;
        ValueText = $"0x{_value.ToString($"X{width}")}";
    }
}
