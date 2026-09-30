using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DogeDebugger.Core.CrossReference;
using DogeDebugger.Core.Modules;
using DogeDebugger.Debugger.Session;

namespace DogeDebugger.UI.ViewModels.Panels;

public partial class CrossRefPanelViewModel : ObservableObject
{
    private readonly DebuggerSession _session;
    private readonly Action<ulong>? _navigateToDisassembly;
    private CancellationTokenSource? _scanCancellation;
    private XrefDatabase? _database;
    private List<FunctionDisplayItem> _allFunctions = [];

    public CrossRefPanelViewModel(
        DebuggerSession session,
        Action<ulong>? navigateToDisassembly = null,
        Func<string, ulong?>? registerLookup = null)
    {
        _session = session;
        _navigateToDisassembly = navigateToDisassembly;
        RegisterLookup = registerLookup;
    }

    public Func<string, ulong?>? RegisterLookup { get; set; }

    public Func<IReadOnlyList<ModuleDescriptor>, ModuleDescriptor?, ModuleDescriptor?>?
        ShowModuleSelectionDialog { get; set; }

    public Action<string, string>? ShowError { get; set; }

    public ModuleDescriptor? AnalyzedModule { get; private set; }

    public XrefDatabase? Database => _database;

    public System.Windows.Visibility ProcessOverlayVisibility =>
        _session.Target.IsOpen
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeModuleCommand))]
    private bool _hasDatabase;

    [ObservableProperty]
    private string _analyzedModuleText = "尚未分析任何模块";

    [ObservableProperty]
    private string _databaseStatsText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeModuleCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopScanCommand))]
    private bool _isScanning;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _progressMessage = string.Empty;

    [ObservableProperty]
    private string _functionFilter = string.Empty;

    [ObservableProperty]
    private ObservableCollection<FunctionDisplayItem> _filteredFunctions = [];

    [ObservableProperty]
    private FunctionDisplayItem? _selectedFunction;

    [ObservableProperty]
    private int _totalFunctions;

    [ObservableProperty]
    private int _totalXrefs;

    [ObservableProperty]
    private string _targetInput = string.Empty;

    [ObservableProperty]
    private string _targetResolvedText =
        "输入地址 / 模块+偏移 / 符号 / 寄存器(RIP) 然后按 Enter 或点击定位";

    [ObservableProperty]
    private ObservableCollection<XrefDisplayItem> _currentReferences = [];

    public void OnTargetChanged()
    {
        StopScan();
        ResetDatabase();
        OnPropertyChanged(nameof(ProcessOverlayVisibility));
    }

    [RelayCommand(CanExecute = nameof(CanAnalyzeModule))]
    private async Task AnalyzeModuleAsync()
    {
        if (!_session.Target.IsOpen)
        {
            ShowError?.Invoke("无法分析模块", "请先打开目标进程。");
            return;
        }

        if (ShowModuleSelectionDialog is null)
        {
            ShowError?.Invoke("无法分析模块", "模块选择窗口尚未初始化。");
            return;
        }

        ModuleDescriptor? module = ShowModuleSelectionDialog(
            _session.EnumerateModules(),
            AnalyzedModule);
        if (module is null)
        {
            return;
        }

        _scanCancellation?.Dispose();
        _scanCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _scanCancellation.Token;
        IsScanning = true;
        Progress = 0;
        ProgressMessage = "准备分析...";
        try
        {
            Progress<(double Progress, string Message)> progress = new(value =>
            {
                Progress = value.Progress;
                ProgressMessage = value.Message;
            });
            XrefDatabase database = await Task.Run(
                    () => _session.BuildModuleCrossReferences(
                        module,
                        pdbPath: null,
                        progress,
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(true);

            cancellationToken.ThrowIfCancellationRequested();
            ApplyDatabase(module, database);
        }
        catch (OperationCanceledException)
        {
            ProgressMessage = "已取消分析";
        }
        catch (Exception exception)
        {
            ProgressMessage = "分析失败";
            ShowError?.Invoke("交叉引用分析失败", exception.Message);
        }
        finally
        {
            IsScanning = false;
            AnalyzeModuleCommand.NotifyCanExecuteChanged();
            StopScanCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanAnalyzeModule() => !IsScanning;

    [RelayCommand(CanExecute = nameof(CanStopScan))]
    private void StopScan()
    {
        CancellationTokenSource? cancellation = _scanCancellation;
        if (cancellation is null || cancellation.IsCancellationRequested)
        {
            return;
        }

        cancellation.Cancel();
    }

    private bool CanStopScan() => IsScanning;

    [RelayCommand]
    private void ResolveTarget()
    {
        if (_database is null || AnalyzedModule is null)
        {
            TargetResolvedText = "请先分析一个模块";
            return;
        }

        string input = TargetInput.Trim();
        if (input.Length == 0)
        {
            TargetResolvedText = "未输入内容";
            return;
        }

        if (!TryResolveAddress(input, out ulong address, out string error))
        {
            TargetResolvedText = $"解析失败: {error}";
            return;
        }

        ulong moduleEnd = AnalyzedModule.BaseAddress + AnalyzedModule.Size;
        if (address < AnalyzedModule.BaseAddress || address >= moduleEnd)
        {
            TargetResolvedText =
                $"地址 0x{address:X} 不在已分析模块 {AnalyzedModule.Name} 范围内";
            return;
        }

        uint rva = checked((uint)(address - AnalyzedModule.BaseAddress));
        FunctionEntry? function = _database.FindFunctionContaining(rva) ??
            _database.FindFunctionAt(rva);
        if (function is null)
        {
            TargetResolvedText =
                $"0x{address:X}: 未识别为任何已枚举函数。仍可对起始 RVA 直接查询 -> 0x{rva:X}";
            RefreshReferencesForRva(rva, $"sub_{address:X}");
            return;
        }

        FunctionEntry entry = function.Value;
        string displayName = entry.DisplayName;
        TargetResolvedText =
            $"定位到函数: {displayName}  @ 0x{AnalyzedModule.BaseAddress + entry.StartRva:X}";
        SelectFunction(entry.StartRva, displayName);
    }

    public void NavigateToDisassembly(ulong address) =>
        _navigateToDisassembly?.Invoke(address);

    public void ActivateFunction(FunctionDisplayItem? item)
    {
        if (item is null)
        {
            return;
        }

        SelectedFunction = item;
    }

    public void CopyFunctionAddress(FunctionDisplayItem? item)
    {
        if (item is not null)
        {
            CopyText($"0x{item.StartVa:X}");
        }
    }

    public void CopyFunctionName(FunctionDisplayItem? item)
    {
        if (item is not null)
        {
            CopyText(item.DisplayName);
        }
    }

    public void NavigateToXrefSource(XrefDisplayItem? item)
    {
        if (item is not null)
        {
            NavigateToDisassembly(item.SourceVa);
        }
    }

    public void NavigateToCaller(XrefDisplayItem? item)
    {
        if (item is not null)
        {
            NavigateToDisassembly(item.CallerStartVa);
        }
    }

    public void CopyXrefAddress(XrefDisplayItem? item)
    {
        if (item is not null)
        {
            CopyText(item.SourceVaHex);
        }
    }

    public void CopyCallerName(XrefDisplayItem? item)
    {
        if (item is not null)
        {
            CopyText(item.CallerName);
        }
    }

    partial void OnFunctionFilterChanged(string value) =>
        RebuildFilteredFunctions();

    partial void OnSelectedFunctionChanged(FunctionDisplayItem? value)
    {
        if (value is null)
        {
            CurrentReferences = [];
            return;
        }

        RefreshReferences(value.StartRva, value.DisplayName);
    }

    private void ApplyDatabase(ModuleDescriptor module, XrefDatabase database)
    {
        _database = database;
        AnalyzedModule = module;
        _allFunctions = [];
        foreach (FunctionEntry function in database.Functions)
        {
            _allFunctions.Add(new FunctionDisplayItem(
                function.StartRva,
                function.EndRva,
                function.DisplayName,
                function.Source,
                database.CountXrefsTo(function.StartRva),
                module.BaseAddress));
        }

        HasDatabase = true;
        TotalFunctions = database.TotalFunctions;
        TotalXrefs = database.TotalXrefs;
        AnalyzedModuleText =
            $"{module.Name}  @ 0x{module.BaseAddress:X}  ({module.SizeText})";
        DatabaseStatsText =
            $"{database.AnalysisDuration.TotalSeconds:F2}s  |  " +
            $"{database.Bitness}-bit  |  {database.TotalXrefs:N0} xrefs";
        TargetResolvedText = "选择左侧函数或在上方输入地址以查看调用方";
        FunctionFilter = string.Empty;
        RebuildFilteredFunctions();
        SelectedFunction = null;
    }

    private void ResetDatabase()
    {
        _database = null;
        AnalyzedModule = null;
        _allFunctions = [];
        FilteredFunctions = [];
        CurrentReferences = [];
        SelectedFunction = null;
        HasDatabase = false;
        TotalFunctions = 0;
        TotalXrefs = 0;
        Progress = 0;
        ProgressMessage = string.Empty;
        AnalyzedModuleText = "尚未分析任何模块";
        DatabaseStatsText = string.Empty;
        TargetResolvedText =
            "输入地址 / 模块+偏移 / 符号 / 寄存器(RIP) 然后按 Enter 或点击定位";
    }

    private void RebuildFilteredFunctions()
    {
        if (_allFunctions.Count == 0)
        {
            FilteredFunctions = [];
            return;
        }

        string filter = FunctionFilter.Trim();
        if (filter.Length == 0)
        {
            FilteredFunctions = new ObservableCollection<FunctionDisplayItem>(
                _allFunctions);
            return;
        }

        string normalized = filter.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? filter[2..]
            : filter;
        List<FunctionDisplayItem> matches = [];
        foreach (FunctionDisplayItem function in _allFunctions)
        {
            if (function.DisplayName.Contains(
                    filter,
                    StringComparison.OrdinalIgnoreCase) ||
                function.StartRva.ToString("X").Contains(
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(function);
            }
        }

        FilteredFunctions = new ObservableCollection<FunctionDisplayItem>(matches);
    }

    private void SelectFunction(uint startRva, string displayName)
    {
        FunctionDisplayItem? function = _allFunctions.FirstOrDefault(
            item => item.StartRva == startRva &&
                    string.Equals(
                        item.DisplayName,
                        displayName,
                        StringComparison.Ordinal));
        if (function is null)
        {
            return;
        }

        if (!FilteredFunctions.Contains(function))
        {
            FunctionFilter = string.Empty;
        }

        SelectedFunction = function;
    }

    private void RefreshReferencesForRva(uint rva, string displayName)
    {
        if (_database is null || AnalyzedModule is null)
        {
            CurrentReferences = [];
            return;
        }

        List<XrefDisplayItem> items = [];
        foreach (XrefRecord record in _database.GetXrefsTo(rva))
        {
            FunctionEntry? caller = _database.FindFunctionContaining(record.SourceRva);
            string callerName = caller?.DisplayName ?? $"sub_{record.SourceRva:X}";
            ulong callerStart = caller is null
                ? AnalyzedModule.BaseAddress + record.SourceRva
                : AnalyzedModule.BaseAddress + caller.Value.StartRva;
            uint callerOffset = caller is null
                ? 0
                : record.SourceRva - caller.Value.StartRva;
            items.Add(new XrefDisplayItem(
                AnalyzedModule.BaseAddress + record.SourceRva,
                FormatXrefType(record.Kind),
                callerName,
                callerOffset,
                callerStart));
        }

        CurrentReferences = new ObservableCollection<XrefDisplayItem>(
            items.OrderBy(item => item.SourceVa));
        if (items.Count == 0)
        {
            TargetResolvedText += "  (无引用)";
        }

        _ = displayName;
    }

    private void RefreshReferences(uint startRva, string displayName) =>
        RefreshReferencesForRva(startRva, displayName);

    private bool TryResolveAddress(
        string input,
        out ulong address,
        out string error)
    {
        if (RegisterLookup?.Invoke(input) is { } registerAddress)
        {
            address = registerAddress;
            error = string.Empty;
            return true;
        }

        if (TryParseNumber(input, out address))
        {
            error = string.Empty;
            return true;
        }

        int separator = input.LastIndexOf('+');
        if (separator > 0 &&
            TryResolveModuleOffset(
                input[..separator].Trim(),
                input[(separator + 1)..].Trim(),
                out address))
        {
            error = string.Empty;
            return true;
        }

        string? symbolName = input;
        int symbolSeparator = input.LastIndexOf('!');
        if (symbolSeparator >= 0 && symbolSeparator + 1 < input.Length)
        {
            symbolName = input[(symbolSeparator + 1)..];
        }

        FunctionEntry? function = _database?.FindFunctionByName(symbolName);
        if (function is { } found && AnalyzedModule is not null)
        {
            address = AnalyzedModule.BaseAddress + found.StartRva;
            error = string.Empty;
            return true;
        }

        address = 0;
        error = "无法识别地址、模块偏移、符号或寄存器";
        return false;
    }

    private bool TryResolveModuleOffset(
        string moduleName,
        string offsetText,
        out ulong address)
    {
        address = 0;
        if (AnalyzedModule is null ||
            !string.Equals(
                moduleName,
                AnalyzedModule.Name,
                StringComparison.OrdinalIgnoreCase) ||
            !TryParseNumber(offsetText, out ulong offset))
        {
            return false;
        }

        address = AnalyzedModule.BaseAddress + offset;
        return true;
    }

    private static bool TryParseNumber(string text, out ulong value)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(
                text[2..],
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out value);
        }

        return ulong.TryParse(
            text,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out value) ||
            ulong.TryParse(
                text,
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out value);
    }

    private static string FormatXrefType(XrefKind kind) => kind switch
    {
        XrefKind.Call => "CALL",
        XrefKind.TailCall => "JMP*",
        XrefKind.Lea => "LEA",
        XrefKind.Jump => "JMP",
        _ => kind.ToString().ToUpperInvariant()
    };

    private static void CopyText(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch
        {
        }
    }
}
