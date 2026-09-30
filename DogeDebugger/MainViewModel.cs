using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DogeDebugger.Core.CrossReference;
using DogeDebugger.Core.Debugger.Stack;
using DogeDebugger.Core.Disassembly;
using DogeDebugger.Core.ExceptionHandler;
using DogeDebugger.Core.Handles;
using DogeDebugger.Core.Memory;
using DogeDebugger.Core.Modules;
using DogeDebugger.Core.Process;
using DogeDebugger.Core.Search;
using DogeDebugger.Core.Scripting.AutoAssembler;
using DogeDebugger.Core.Signatures;
using DogeDebugger.Core.Settings;
using DogeDebugger.Core.StringRef;
using DogeDebugger.Core.Trace;
using DogeDebugger.Core.Threading;
using DogeDebugger.Debugger.Breakpoints;
using DogeDebugger.Debugger.Plugins;
using DogeDebugger.Debugger.Scripting;
using DogeDebugger.Debugger.Session;
using DogeDebugger.Debugger.Trace;
using DogeDebugger.Debugger.UserMode;
using DogeDebugger.Core.AI;
using DogeDebugger.Plugins.CEMono.UI;
using DogeDebugger.Plugins.UnrealEngine.UI;
using DogeDebugger.UI.ViewModels.Panels;
using DogeDebugger.UI.Views.Panels;
using MoonSharp.Interpreter;

namespace DogeDebugger;

public partial class MainViewModel : ObservableObject
{
    private static readonly string? DiagnosticLogPath =
        Environment.GetEnvironmentVariable("DOGEDEBUGGER_DIAGNOSTIC_LOG");
    private readonly DebuggerSession _session;
    private readonly SettingsStore _settings;
    private readonly McpServer _mcpServer;
    private readonly PluginRuntime _pluginRuntime;
    private readonly LuaDebuggerScriptHost _luaHost;
    private AutoAssemblerSymbolResolver? _autoAssemblerSymbolResolver;
    private AutoAssemblerEngine? _autoAssemblerEngine;
    private readonly TraceController _trace;
    private readonly Dispatcher _dispatcher;
    private readonly Stack<ulong> _backAddressHistory = [];
    private readonly Stack<ulong> _forwardAddressHistory = [];
    private CallStackWalker? _callStackWalker;
    private RegisterSnapshot? _lastPauseRegisters;
    private uint _lastPauseThreadId;
    private ulong _callStackReferenceAddress;
    private ulong? _currentAddress;

    [ObservableProperty]
    private ProcessDescriptor? _selectedProcess;

    [ObservableProperty]
    private ModuleDescriptor? _selectedModule;

    [ObservableProperty]
    private PeModuleMetadata? _selectedModuleMetadata;

    [ObservableProperty]
    private MemoryRegionInfo? _selectedMemoryRegion;

    [ObservableProperty]
    private ThreadDescriptor? _selectedThread;

    [ObservableProperty]
    private ProcessHandleEntry? _selectedHandle;

    [ObservableProperty]
    private BreakpointEntry? _selectedBreakpoint;

    [ObservableProperty]
    private StringEntry? _selectedString;

    [ObservableProperty]
    private bool _includeUnicodeInStringScan = true;

    [ObservableProperty]
    private XrefEntry? _selectedCrossReference;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunToCursorCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleBreakpointCommand))]
    private InstructionSnapshot? _selectedInstruction;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestartCommand))]
    private string _launchCommandLine = string.Empty;

    [ObservableProperty]
    private string _addressInput = "0x";

    [ObservableProperty]
    private string _searchPattern = string.Empty;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string _targetText = "No target";

    [ObservableProperty]
    private string _debuggerStateText = "Detached";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PassExceptionCommand))]
    private DebuggerPauseReason? _lastPauseReason;

    [ObservableProperty]
    private string _mcpStateText = "MCP stopped";

    [ObservableProperty]
    private int _searchResultCount;

    [ObservableProperty]
    private int _loadedPluginCount;

    [ObservableProperty]
    private string _notesText = string.Empty;

    [ObservableProperty]
    private string _processNotesText = string.Empty;

    [ObservableProperty]
    private string _autoAssemblerText = string.Empty;

    [ObservableProperty]
    private string _autoAssemblerOutputText = string.Empty;

    [ObservableProperty]
    private string _autoAssemblerStatusText = "就绪";

    [ObservableProperty]
    private bool _isAutoAssemblerActive;

    [ObservableProperty]
    private string _luaScriptText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunLuaCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopLuaCommand))]
    private bool _isLuaRunning;

    [ObservableProperty]
    private string _luaOutputText = string.Empty;

    [ObservableProperty]
    private string _luaScriptPath = "editor.lua";

    [ObservableProperty]
    private string _luaStatusText = "就绪";

    [ObservableProperty]
    private int _luaCaretLine = 1;

    [ObservableProperty]
    private int _luaCaretColumn = 1;

    [ObservableProperty]
    private string _luaProblemsText = string.Empty;

    [ObservableProperty]
    private int _luaProblemCount;

    [ObservableProperty]
    private string _luaAiRequirement = string.Empty;

    [ObservableProperty]
    private string _luaAiStatusText =
        "AI is not configured. Please set API URL and API Key in Settings.";

    [ObservableProperty]
    private string _luaAiPreview = string.Empty;

    [ObservableProperty]
    private bool _isLuaAiBusy;

    public IReadOnlyList<LuaApiEntry> LuaApiEntries => LuaApiCatalog.Entries;

    [ObservableProperty]
    private bool _isSourceDebuggingEnabled;

    [ObservableProperty]
    private string _sourceRoot = string.Empty;

    [ObservableProperty]
    private string _pdbRoot = string.Empty;

    [ObservableProperty]
    private string _sourceDocumentText = string.Empty;

    [ObservableProperty]
    private string _sourceDocumentTitle = "未打开源码文档";

    [ObservableProperty]
    private string _aiPrompt = string.Empty;

    [ObservableProperty]
    private string _traceSummaryText = "暂无追踪结果";

    [ObservableProperty]
    private double _traceProgress;

    [ObservableProperty]
    private bool _isTraceRunning;

    [ObservableProperty]
    private string _exceptionHandlerStatusText = "尚未扫描异常处理结构。";

    [ObservableProperty]
    private bool _isExceptionHandlerScanning;

    [ObservableProperty]
    private bool _hasExceptionHandlerResult;

    public MainViewModel(
        DebuggerSession session,
        SettingsStore settings,
        McpServer mcpServer,
        PluginRuntime pluginRuntime)
    {
        _session = session;
        _settings = settings;
        _mcpServer = mcpServer;
        _pluginRuntime = pluginRuntime;
        _luaHost = new LuaDebuggerScriptHost(session);
        _dispatcher = Dispatcher.CurrentDispatcher;
        _session.Paused += HandlePaused;
        _session.Debugger.Diagnostic += HandleDebuggerDiagnostic;
        _trace = new TraceController(session);
        _trace.ProgressChanged += HandleTraceProgressChanged;
        _trace.Completed += HandleTraceCompleted;
        _session.StateChanged += HandleStateChanged;
        _session.TargetChanged += HandleTargetChanged;
        _mcpServer.Diagnostic += (_, message) => McpStateText = message;
        NotesText = _settings.Current.GlobalNotes;
        IsSourceDebuggingEnabled = _settings.Current.SourceDebugging.Enabled;
        SourceRoot = _settings.Current.SourceDebugging.SourceRoot;
        PdbRoot = _settings.Current.SourceDebugging.PdbRoot;
        MemorySearchWorkspace = new MemorySearchWorkspaceViewModel(
            session,
            address => _ = ShowAddressAsync(address));
        RttiPanel = new RttiViewModel(session);
        MonoPanel = new MonoExplorerViewModel(
            session,
            address => _ = ShowAddressAsync(address),
            (name, il) => AppendLog($"[Mono IL] {name}{Environment.NewLine}{il}"),
            RegisterCustomSymbol);
        UnrealPanel = new UnrealExplorerViewModel(
            session,
            address => _ = ShowAddressAsync(address),
            RegisterCustomSymbol);
        CrossRefPanel = new CrossRefPanelViewModel(
            session,
            address => _ = ShowAddressAsync(address),
            name => TryResolveRegister(name, out ulong value) ? value : null);
        SourceDebugPanel = new SourceDebugPanelViewModel(session, settings);
    }

    internal SettingsStore Settings => _settings;

    public ObservableCollection<ProcessDescriptor> Processes { get; } = [];

    public ObservableCollection<ModuleDescriptor> Modules { get; } = [];

    public ObservableCollection<ModuleImportRow> SelectedModuleImports { get; } = [];

    public ObservableCollection<ModuleExportRow> SelectedModuleExports { get; } = [];

    public ObservableCollection<MemoryRegionInfo> MemoryRegions { get; } = [];

    public ObservableCollection<ThreadDescriptor> Threads { get; } = [];

    public ObservableCollection<BreakpointEntry> Breakpoints { get; } = [];

    public ObservableCollection<InstructionSnapshot> Disassembly { get; } = [];

    public ObservableCollection<HexViewLine> HexLines { get; } = [];

    public ObservableCollection<RegisterRow> Registers { get; } = [];

    public ObservableCollection<SearchMatch> SearchMatches { get; } = [];

    public ObservableCollection<ProcessHandleEntry> Handles { get; } = [];

    public ObservableCollection<XrefEntry> CrossReferences { get; } = [];

    public ObservableCollection<StringEntry> StringReferences { get; } = [];

    public ObservableCollection<ulong> SelectedStringReferences { get; } = [];

    public ObservableCollection<string> SourceDocuments { get; } = [];

    public ObservableCollection<string> SourceLocals { get; } = [];

    public ObservableCollection<string> SourceBreakpoints { get; } = [];

    public ObservableCollection<DebuggerEventRecord> DebugEvents { get; } = [];

    public ObservableCollection<CallStackRow> CallStack { get; } = [];

    public ObservableCollection<CommentRow> Comments { get; } = [];

    public ObservableCollection<CustomSymbolRow> CustomSymbols { get; } = [];

    public ObservableCollection<VehEntry> VehEntries { get; } = [];

    public ObservableCollection<PdataSehEntry> PdataEntries { get; } = [];

    public ObservableCollection<HookDetectionEntry> HookEntries { get; } = [];

    public ObservableCollection<LogRow> LogMessages { get; } = [];

    public ObservableCollection<TraceStep> TraceSteps { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NavigateToCallStackFrameCommand))]
    [NotifyCanExecuteChangedFor(nameof(UseSelectedCallStackRowAsReferenceCommand))]
    private CallStackRow? _selectedCallStackRow;

    [ObservableProperty]
    private CallStackDisplayMode _callStackDisplayMode = CallStackDisplayMode.StackTrace;

    [ObservableProperty]
    private CallStackReferenceBase _callStackReferenceBase = CallStackReferenceBase.Rsp;

    [ObservableProperty]
    private int _maxStackBytes = 512;

    [ObservableProperty]
    private string _callStackStatusText = "等待调试器中断...";

    [ObservableProperty]
    private int _callStackFrameCount;

    public string CallStackFirstHeader =>
        CallStackDisplayMode == CallStackDisplayMode.StackTrace ? "返回地址" : "地址";

    public string CallStackSecondHeader =>
        CallStackDisplayMode == CallStackDisplayMode.StackTrace ? "函数" : "值";

    public string CallStackThirdHeader =>
        CallStackDisplayMode == CallStackDisplayMode.StackTrace ? "参数" : "偏移";

    public bool IsCallStackModeStackTrace =>
        CallStackDisplayMode == CallStackDisplayMode.StackTrace;

    public bool IsCallStackModeFullStack =>
        CallStackDisplayMode == CallStackDisplayMode.FullStack;

    public bool IsCallStackModeModulesOnly =>
        CallStackDisplayMode == CallStackDisplayMode.ModulesOnly;

    public bool IsCallStackModeNonSystemModulesOnly =>
        CallStackDisplayMode == CallStackDisplayMode.NonSystemModulesOnly;

    public bool IsCallStackReferenceRsp =>
        CallStackReferenceBase == CallStackReferenceBase.Rsp;

    public bool IsCallStackReferenceRbp =>
        CallStackReferenceBase == CallStackReferenceBase.Rbp;

    public bool IsCallStackReferenceSelectedRow =>
        CallStackReferenceBase == CallStackReferenceBase.SelectedRow;

    public string CallStackReferenceText =>
        _callStackReferenceAddress == 0
            ? string.Empty
            : $"0x{_callStackReferenceAddress:X}";

    public MemorySearchWorkspaceViewModel MemorySearchWorkspace { get; }

    public RttiViewModel RttiPanel { get; }

    public MonoExplorerViewModel MonoPanel { get; }

    public UnrealExplorerViewModel UnrealPanel { get; }

    public CrossRefPanelViewModel CrossRefPanel { get; }

    public SourceDebugPanelViewModel SourceDebugPanel { get; }

    public MemorySearchViewModel MemorySearch => MemorySearchWorkspace.ActiveTab;

    public bool IsTargetOpen => _session.Target.IsOpen;

    public bool IsTarget64Bit => _session.Target.Is64Bit;

    public bool IsDebuggerPaused => _session.Debugger.IsPaused;

    public bool CanGoBack => _backAddressHistory.Count > 0;

    public bool CanGoForward => _forwardAddressHistory.Count > 0;

    public int TargetProcessId => _session.Target.ProcessId;

    public string TargetProcessName => _session.Target.ProcessName;

    public string TargetProcessPath => _session.Target.FilePath;

    public TraceSession? CurrentTraceSession { get; private set; }

    public bool HasTraceResult => CurrentTraceSession is not null;

    public event Action<int, int>? TraceProgressChanged;

    public event Action<TraceSession>? TraceCompleted;

    public async Task InitializeAsync()
    {
        await RefreshProcessesAsync().ConfigureAwait(true);
        string pluginDirectory = Path.Combine(AppContext.BaseDirectory, "Plugins");
        Dictionary<string, bool> pluginStates = _settings.Current.PluginStates.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.IsEnabled,
            StringComparer.OrdinalIgnoreCase);
        await _pluginRuntime.LoadDirectoryAsync(pluginDirectory, pluginStates)
            .ConfigureAwait(true);
        LoadedPluginCount = _pluginRuntime.Plugins.Count;
        if (_settings.Current.McpEnabled)
        {
            try
            {
                await _mcpServer.StartAsync(
                        _settings.Current.McpListenAddress,
                        _settings.Current.McpPort)
                    .ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                McpStateText = $"MCP failed: {exception.Message}";
            }
        }
    }

    public async Task<PointerScanResult> ScanPointersAsync(
        PointerScanOptions options,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(
                () => _session.ScanPointers(options, cancellationToken),
                cancellationToken)
            .ConfigureAwait(true);
    }

    public async Task<PointerScanResult> RescanPointersAsync(
        PointerScanResult previous,
        PointerScanRescanOptions options,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(
                () => _session.RescanPointers(
                    previous,
                    options,
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(true);
    }

    public ITargetProcess Target => _session.Target;

    public IReadOnlyList<ModuleDescriptor> EnumerateTargetModules() =>
        _session.EnumerateModules();

    public bool TryResolveRegister(string name, out ulong value)
    {
        value = 0;
        if (_lastPauseRegisters is null ||
            string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string registerName = name.Trim().TrimStart('$').ToUpperInvariant();
        if (registerName == "IP" || registerName == "RIP" ||
            registerName == "EIP")
        {
            value = _lastPauseRegisters.InstructionPointer;
            return value != 0;
        }

        if (registerName == "SP" || registerName == "RSP" ||
            registerName == "ESP")
        {
            value = _lastPauseRegisters.StackPointer;
            return value != 0;
        }

        if (registerName == "BP" || registerName == "RBP" ||
            registerName == "EBP")
        {
            value = _lastPauseRegisters.FramePointer;
            return value != 0;
        }

        if (_lastPauseRegisters.GeneralPurposeRegisters.TryGetValue(
                registerName,
                out value))
        {
            return true;
        }

        return false;
    }

    public async Task<AobSignature?> GenerateSignatureAsync(
        ulong address,
        ModuleDescriptor? module,
        AobSignatureOptions options,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(
                () => _session.GenerateSignature(
                    address,
                    module,
                    options,
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(true);
    }

    public Task NavigateToAddressAsync(ulong address) => ShowAddressAsync(address);

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private async Task GoBackAsync()
    {
        if (!_backAddressHistory.TryPop(out ulong address))
        {
            return;
        }

        if (_currentAddress is { } current)
        {
            _forwardAddressHistory.Push(current);
        }

        await ShowAddressAsync(address, recordHistory: false).ConfigureAwait(true);
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private async Task GoForwardAsync()
    {
        if (!_forwardAddressHistory.TryPop(out ulong address))
        {
            return;
        }

        if (_currentAddress is { } current)
        {
            _backAddressHistory.Push(current);
        }

        await ShowAddressAsync(address, recordHistory: false).ConfigureAwait(true);
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }

    public ulong AllocateMemory(
        ulong preferredAddress,
        int size,
        uint protection)
    {
        return _session.AllocateMemory(preferredAddress, size, protection);
    }

    public bool FreeMemory(ulong address)
    {
        return _session.FreeMemory(address);
    }

    public int CreateRemoteThread(
        ulong startAddress,
        ulong parameter,
        uint creationFlags)
    {
        return _session.CreateRemoteThread(startAddress, parameter, creationFlags);
    }

    public uint CallRemoteFunction(
        ulong functionAddress,
        IReadOnlyList<ulong> arguments)
    {
        return _session.CallRemoteFunction(functionAddress, arguments);
    }

    public Task<RemoteOperationResult> CallRemoteFunction64Async(
        ulong functionAddress,
        IReadOnlyList<ulong> arguments,
        uint creationFlags = 0,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => _session.CallRemoteFunction64(
                functionAddress,
                arguments,
                creationFlags: creationFlags),
            cancellationToken);
    }

    public ulong InjectDll(string path)
    {
        return _session.InjectDll(path);
    }

    public Task<RemoteOperationResult> InjectDllAsync(
        string path,
        string? exportName,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => _session.InjectDll(path, exportName),
            cancellationToken);
    }

    public Task<RemoteOperationResult> UnloadDllAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => _session.UnloadDll(path),
            cancellationToken);
    }

    public async Task<string?> StartTraceAsync(
        TraceStopCondition condition,
        TraceMode mode)
    {
        TraceSteps.Clear();
        CurrentTraceSession = null;
        TraceProgress = 0;
        OnPropertyChanged(nameof(HasTraceResult));
        string? error = await _trace.StartAsync(condition, mode).ConfigureAwait(true);
        if (error is null)
        {
            IsTraceRunning = true;
            TraceSummaryText = "追踪中...";
        }

        return error;
    }

    public async Task StopTraceAsync()
    {
        await _trace.StopAsync().ConfigureAwait(true);
    }

    public void ClearTraceResult()
    {
        CurrentTraceSession = null;
        TraceSteps.Clear();
        TraceProgress = 0;
        TraceSummaryText = "暂无追踪结果";
        OnPropertyChanged(nameof(HasTraceResult));
    }

    [RelayCommand]
    private async Task RefreshProcessesAsync()
    {
        try
        {
            IReadOnlyList<ProcessDescriptor> processes = _session.EnumerateProcesses();
            Processes.Clear();
            foreach (ProcessDescriptor process in processes)
            {
                Processes.Add(process);
            }

            StatusText = $"Enumerated {Processes.Count} processes.";
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }

        await Task.CompletedTask;
    }

    [RelayCommand]
    private void RefreshTargetPanels()
    {
        RefreshPanels();
    }

    [RelayCommand]
    private void SuspendSelectedThread()
    {
        if (SelectedThread is null)
        {
            return;
        }

        _session.SetThreadSuspended(SelectedThread.ThreadId, suspended: true);
        RefreshPanels();
    }

    [RelayCommand]
    private void ResumeSelectedThread()
    {
        if (SelectedThread is null)
        {
            return;
        }

        _session.SetThreadSuspended(SelectedThread.ThreadId, suspended: false);
        RefreshPanels();
    }

    [RelayCommand]
    private async Task OpenSelectedThreadAsync()
    {
        if (SelectedThread is not null && SelectedThread.StartAddress != 0)
        {
            await ShowAddressAsync(SelectedThread.StartAddress);
        }
    }

    [RelayCommand]
    private async Task OpenSelectedHandleAsync()
    {
        if (SelectedHandle is not null && SelectedHandle.ObjectAddress != 0)
        {
            await ShowAddressAsync(SelectedHandle.ObjectAddress);
        }
    }

    [RelayCommand]
    private void ClearBreakpoints()
    {
        _session.Breakpoints.Clear();
        RefreshBreakpoints();
    }

    [RelayCommand]
    private void ToggleSelectedBreakpoint()
    {
        if (SelectedBreakpoint is null)
        {
            return;
        }

        if (SelectedBreakpoint.IsEnabled)
        {
            _session.Breakpoints.Disable(SelectedBreakpoint.Address);
        }
        else
        {
            _session.Breakpoints.Enable(SelectedBreakpoint.Address);
        }

        RefreshBreakpoints();
    }

    public void ToggleBreakpointEnabled(BreakpointEntry breakpoint)
    {
        ArgumentNullException.ThrowIfNull(breakpoint);
        bool changed = breakpoint.IsEnabled
            ? _session.Breakpoints.Disable(breakpoint.Address)
            : _session.Breakpoints.Enable(breakpoint.Address);
        if (changed)
        {
            RefreshBreakpoints();
        }
    }

    [RelayCommand]
    private void RemoveSelectedBreakpoint()
    {
        if (SelectedBreakpoint is null)
        {
            return;
        }

        _session.Breakpoints.Remove(SelectedBreakpoint.Address);
        RefreshBreakpoints();
    }

    [RelayCommand]
    private void ClearComments()
    {
        Comments.Clear();
    }

    [RelayCommand]
    private void ClearCustomSymbols()
    {
        CustomSymbols.Clear();
    }

    [RelayCommand(CanExecute = nameof(CanLoadSymbols))]
    private async Task LoadSymbolsAsync()
    {
        await SourceDebugPanel.RefreshIndexCommand.ExecuteAsync(null);
    }

    private bool CanLoadSymbols() =>
        IsTargetOpen;

    [RelayCommand(CanExecute = nameof(CanCloseProcessHandle))]
    private async Task CloseProcessHandleAsync()
    {
        await _session.CloseAsync();
    }

    private bool CanCloseProcessHandle() =>
        IsTargetOpen;

    public void RegisterCustomSymbol(ulong address, string name)
    {
        if (address == 0 || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        string normalizedName = name.Trim();
        foreach (CustomSymbolRow existing in CustomSymbols
                     .Where(row =>
                         row.Address == address ||
                         string.Equals(
                             row.Name,
                             normalizedName,
                             StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            CustomSymbols.Remove(existing);
        }

        CustomSymbols.Add(new CustomSymbolRow(address, normalizedName));
        _autoAssemblerSymbolResolver?.RegisterSymbol(normalizedName, address);
    }

    public string AutoAssemblerActiveScriptText => IsAutoAssemblerActive ? "True" : "False";

    public System.Windows.Visibility AutoAssemblerOverlayVisibility =>
        IsTargetOpen ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public System.Windows.Visibility ProcessOverlayVisibility =>
        IsTargetOpen ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    partial void OnIsAutoAssemblerActiveChanged(bool value) =>
        OnPropertyChanged(nameof(AutoAssemblerActiveScriptText));

    [RelayCommand]
    private async Task EnableAutoAssemblerAsync()
    {
        if (_autoAssemblerEngine is null)
        {
            AppendAutoAssemblerLog("请先打开目标进程。");
            return;
        }

        AutoAssemblerEngine engine = _autoAssemblerEngine;
        string script = AutoAssemblerText;
        AutoAssemblerResult result = await Task.Run(
                () => engine.Execute(script, enable: true),
                CancellationToken.None)
            .ConfigureAwait(true);
        ApplyAutoAssemblerResult(result, enable: true);
    }

    [RelayCommand]
    private async Task DisableAutoAssemblerAsync()
    {
        if (_autoAssemblerEngine is null)
        {
            AppendAutoAssemblerLog("请先打开目标进程。");
            return;
        }

        AutoAssemblerEngine engine = _autoAssemblerEngine;
        string script = AutoAssemblerText;
        AutoAssemblerResult result = await Task.Run(
                () => engine.Execute(script, enable: false),
                CancellationToken.None)
            .ConfigureAwait(true);
        ApplyAutoAssemblerResult(result, enable: false);
    }

    [RelayCommand]
    private void ClearAutoAssemblerOutput()
    {
        AutoAssemblerOutputText = "AA 输出已清空。";
    }

    public void InsertAutoAssemblerBasicTemplate()
    {
        AutoAssemblerText = AutoAssemblerTemplates.CreateBasicScript();
        AppendAutoAssemblerLog("已插入基础脚本模板。");
    }

    public void InsertAutoAssemblerInjectionTemplate(string addressText, int jumpLength)
    {
        string normalizedAddress = addressText.Trim();
        IReadOnlyList<AutoAssemblerInstruction> instructions =
            ReadAutoAssemblerInstructions(normalizedAddress, jumpLength);
        AutoAssemblerText = AutoAssemblerTemplates.CreateInjectionScript(
            normalizedAddress,
            instructions,
            jumpLength);
        AppendAutoAssemblerLog(
            $"已插入 代码注入({jumpLength}字节跳转) 模板 (地址: {normalizedAddress})。");
    }

    public void InsertAutoAssemblerAobTemplate(string symbol)
    {
        AutoAssemblerText = AutoAssemblerTemplates.CreateAobScript(symbol, string.Empty, "90 90");
        AppendAutoAssemblerLog($"已插入 AOB注入 模板 (符号: {symbol})。");
    }

    public string GetAutoAssemblerSuggestedAddress()
    {
        IReadOnlyList<ModuleDescriptor> modules = Modules.Count > 0
            ? [.. Modules]
            : _session.EnumerateModules();
        string processName = _session.Target.IsOpen ? _session.Target.ProcessName : string.Empty;
        ModuleDescriptor? primaryModule = FindPrimaryModule(modules);
        if (_currentAddress is { } address &&
            !string.IsNullOrWhiteSpace(processName) &&
            primaryModule is not null &&
            address >= primaryModule.BaseAddress &&
            address < primaryModule.BaseAddress + primaryModule.Size)
        {
            return $"\"{primaryModule.Name}\"+0x{address - primaryModule.BaseAddress:X}";
        }

        if (primaryModule is not null)
        {
            ulong entry = primaryModule.EntryPoint > 0
                ? primaryModule.EntryPoint
                : primaryModule.BaseAddress;
            if (TryFormatModuleAddress(entry, modules, out string? entryText))
            {
                return entryText!;
            }
        }

        return string.IsNullOrWhiteSpace(processName)
            ? "\"process.exe\"+0x1234"
            : $"\"{processName}\"+0x1234";
    }

    private static bool TryFormatModuleAddress(
        ulong address,
        IReadOnlyList<ModuleDescriptor> modules,
        out string? text)
    {
        foreach (ModuleDescriptor module in modules)
        {
            if (address >= module.BaseAddress && address < module.BaseAddress + module.Size)
            {
                text = $"\"{module.Name}\"+0x{address - module.BaseAddress:X}";
                return true;
            }
        }

        text = null;
        return false;
    }

    private ModuleDescriptor? FindPrimaryModule(IReadOnlyList<ModuleDescriptor> modules)
    {
        ModuleDescriptor? main = modules.FirstOrDefault(static module => module.IsMainModule);
        if (main is not null)
        {
            return main;
        }

        string targetPath = _session.Target.FilePath;
        string targetName = _session.Target.ProcessName;
        ModuleDescriptor? byPath = modules.FirstOrDefault(module =>
            !string.IsNullOrEmpty(targetPath) &&
            !string.IsNullOrEmpty(module.FilePath) &&
            string.Equals(
                Path.GetFileName(module.FilePath),
                Path.GetFileName(targetPath),
                StringComparison.OrdinalIgnoreCase));
        if (byPath is not null)
        {
            return byPath;
        }

        ModuleDescriptor? byName = modules.FirstOrDefault(module =>
            !string.IsNullOrWhiteSpace(targetName) &&
            string.Equals(
                Path.GetFileNameWithoutExtension(module.Name),
                targetName,
                StringComparison.OrdinalIgnoreCase));
        return byName ?? modules.FirstOrDefault();
    }

    private IReadOnlyList<AutoAssemblerInstruction> ReadAutoAssemblerInstructions(
        string addressText,
        int jumpLength)
    {
        if (_autoAssemblerSymbolResolver is null || !_session.Target.IsOpen)
        {
            return [];
        }

        string symbol = addressText.Trim().Trim('"');
        if (!_autoAssemblerSymbolResolver.TryResolve(symbol, out ulong address))
        {
            return [];
        }

        int size = Math.Min(jumpLength * 3, 64);
        byte[] bytes = _session.Target.ReadBytes(address, size);
        if (bytes.Length == 0)
        {
            return [];
        }

        Iced.Intel.ByteArrayCodeReader reader = new(bytes);
        Iced.Intel.Decoder decoder = Iced.Intel.Decoder.Create(
            _session.Target.Is64Bit ? 64 : 32,
            reader);
        decoder.IP = address;
        Iced.Intel.MasmFormatter formatter = new();
        List<AutoAssemblerInstruction> instructions = [];
        int totalBytes = 0;
        ulong endAddress = address + (ulong)bytes.Length;
        while (decoder.IP < endAddress && totalBytes < jumpLength)
        {
            Iced.Intel.Instruction instruction = decoder.Decode();
            if (instruction.IsInvalid || instruction.Length == 0)
            {
                break;
            }

            Iced.Intel.StringOutput output = new();
            formatter.Format(instruction, output);
            instructions.Add(new AutoAssemblerInstruction(output.ToString(), instruction.Length));
            totalBytes += instruction.Length;
        }

        return instructions;
    }

    private void ApplyAutoAssemblerResult(AutoAssemblerResult result, bool enable)
    {
        if (result.Success)
        {
            IsAutoAssemblerActive = enable;
            AutoAssemblerStatusText = "执行成功";
            AppendAutoAssemblerLog(enable ? "脚本已启用。" : "脚本已禁用。");
        }
        else
        {
            if (enable)
            {
                IsAutoAssemblerActive = false;
            }

            AutoAssemblerStatusText = "执行失败";
            AppendAutoAssemblerLog($"错误: 第 {result.ErrorLine} 行 : {result.ErrorMessage}");
        }

        if (result.Trace.Count > 0)
        {
            AppendAutoAssemblerLog("--- Trace Log ---");
            foreach (string entry in result.Trace)
            {
                AppendAutoAssemblerLog(entry);
            }

            AppendAutoAssemblerLog("--- End Trace ---");
        }
    }

    private void AppendAutoAssemblerLog(string message)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        string entry = $"[{timestamp}] {message}";
        AutoAssemblerOutputText = string.IsNullOrEmpty(AutoAssemblerOutputText)
            ? entry
            : $"{AutoAssemblerOutputText}\n{entry}";
    }

    public void UpdateMaxStackBytes(int value) =>
        MaxStackBytes = Math.Clamp(value, 512, 1024);

    partial void OnCallStackDisplayModeChanged(CallStackDisplayMode value)
    {
        OnPropertyChanged(nameof(CallStackFirstHeader));
        OnPropertyChanged(nameof(CallStackSecondHeader));
        OnPropertyChanged(nameof(CallStackThirdHeader));
        OnPropertyChanged(nameof(IsCallStackModeStackTrace));
        OnPropertyChanged(nameof(IsCallStackModeFullStack));
        OnPropertyChanged(nameof(IsCallStackModeModulesOnly));
        OnPropertyChanged(nameof(IsCallStackModeNonSystemModulesOnly));
        RebuildCallStack();
    }

    partial void OnCallStackReferenceBaseChanged(CallStackReferenceBase value)
    {
        OnPropertyChanged(nameof(IsCallStackReferenceRsp));
        OnPropertyChanged(nameof(IsCallStackReferenceRbp));
        OnPropertyChanged(nameof(IsCallStackReferenceSelectedRow));
        RebuildCallStack();
    }

    partial void OnMaxStackBytesChanged(int value) => RebuildCallStack();

    [RelayCommand]
    private async Task RefreshCallStackAsync()
    {
        if (_callStackWalker is null || _lastPauseRegisters is null)
        {
            CallStackStatusText = "等待调试器中断...";
            return;
        }

        try
        {
            RegisterSnapshot snapshot = _lastPauseRegisters;
            IReadOnlyList<CallStackFrame> frames = await Task.Run(
                    () => _callStackWalker.Walk(
                        snapshot,
                        CallStackDisplayMode,
                        CallStackReferenceBase,
                        MaxStackBytes,
                        _callStackReferenceAddress),
                    CancellationToken.None)
                .ConfigureAwait(true);
            ApplyCallStackFrames(frames);
        }
        catch (Exception exception)
        {
            CallStackStatusText = "读取调用堆栈失败：" + exception.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanNavigateToCallStackFrame))]
    private void NavigateToCallStackFrame()
    {
        if (SelectedCallStackRow is not { } row)
        {
            return;
        }

        ulong target = row.PointerTarget != 0 ? row.PointerTarget : row.Address;
        if (target != 0)
        {
            _ = ShowAddressAsync(target);
        }
    }

    private bool CanNavigateToCallStackFrame() => SelectedCallStackRow is not null;

    [RelayCommand(CanExecute = nameof(CanNavigateToCallStackFrame))]
    private void UseSelectedCallStackRowAsReference()
    {
        if (SelectedCallStackRow is not { } row)
        {
            return;
        }

        _callStackReferenceAddress = row.SlotAddress;
        OnPropertyChanged(nameof(CallStackReferenceText));
        CallStackReferenceBase = CallStackReferenceBase.SelectedRow;
    }

    [RelayCommand]
    private void CopyCallStackAddress()
    {
        if (SelectedCallStackRow is { } row)
        {
            System.Windows.Clipboard.SetText($"0x{row.PrimaryAddress:X}");
        }
    }

    [RelayCommand]
    private void CopyFullCallStack() => CopyCallStackRows(CallStack);

    [RelayCommand]
    private void SetCallStackDisplayMode(CallStackDisplayMode mode) =>
        CallStackDisplayMode = mode;

    [RelayCommand]
    private void SetCallStackReferenceBase(CallStackReferenceBase referenceBase) =>
        CallStackReferenceBase = referenceBase;

    internal static string FormatCallStackRow(CallStackRow row) =>
        $"#{row.Index,-4} {row.First,-28} {row.Second,-24} {row.Third}";

    internal static void CopyCallStackRows(IEnumerable<CallStackRow> rows)
    {
        string text = string.Join(
            Environment.NewLine,
            rows.Select(FormatCallStackRow));
        if (text.Length > 0)
        {
            System.Windows.Clipboard.SetText(text);
        }
    }

    private void ResetCallStack()
    {
        _lastPauseRegisters = null;
        _lastPauseThreadId = 0;
        _callStackReferenceAddress = 0;
        OnPropertyChanged(nameof(CallStackReferenceText));
        CallStack.Clear();
        CallStackFrameCount = 0;
        CallStackStatusText = "等待调试器中断...";
    }

    private void RebuildCallStack()
    {
        if (_callStackWalker is null || _lastPauseRegisters is null || !_session.Target.IsOpen)
        {
            CallStack.Clear();
            CallStackFrameCount = 0;
            if (_lastPauseRegisters is null)
            {
                CallStackStatusText = "等待调试器中断...";
            }

            return;
        }

        try
        {
            IReadOnlyList<CallStackFrame> frames = _callStackWalker.Walk(
                _lastPauseRegisters,
                CallStackDisplayMode,
                CallStackReferenceBase,
                MaxStackBytes,
                _callStackReferenceAddress);
            ApplyCallStackFrames(frames);
        }
        catch (Exception exception)
        {
            CallStack.Clear();
            CallStackFrameCount = 0;
            CallStackStatusText = "读取调用堆栈失败：" + exception.Message;
        }
    }

    private void ApplyCallStackFrames(IReadOnlyList<CallStackFrame> frames)
    {
        bool stackTrace = CallStackDisplayMode == CallStackDisplayMode.StackTrace;
        IReadOnlyList<ModuleDescriptor> modules = stackTrace
            ? []
            : _session.EnumerateModules();
        IEnumerable<CallStackFrame> display = CallStackDisplayMode switch
        {
            CallStackDisplayMode.ModulesOnly => frames.Where(
                static frame => frame.IsPointerIntoModule),
            CallStackDisplayMode.NonSystemModulesOnly => FilterNonSystemModules(frames, modules),
            _ => frames
        };

        CallStack.Clear();
        int index = 0;
        foreach (CallStackFrame frame in display)
        {
            CallStack.Add(new CallStackRow(
                index++,
                frame.Address,
                frame.SlotAddress,
                stackTrace ? $"0x{frame.Address:X}" : $"0x{frame.SlotAddress:X}",
                stackTrace ? frame.Symbol : $"0x{frame.Address:X}",
                frame.ReferenceText,
                stackTrace ? frame.Address : frame.SlotAddress,
                frame.PointerTarget));
        }

        CallStackFrameCount = CallStack.Count;
        if (!_session.Debugger.IsPaused)
        {
            CallStackStatusText = "等待调试器中断...";
            return;
        }

        if (stackTrace)
        {
            CallStackStatusText = $"跟踪堆栈 | {CallStack.Count} 帧 | 线程 {_lastPauseThreadId}";
            return;
        }

        string modeText = CallStackDisplayMode switch
        {
            CallStackDisplayMode.FullStack => "完整堆栈",
            CallStackDisplayMode.ModulesOnly => "只显示模块",
            CallStackDisplayMode.NonSystemModulesOnly => "只显示非系统模块",
            _ => string.Empty
        };
        CallStackStatusText =
            $"{modeText} | {CallStack.Count} / {frames.Count} 行 | 线程 {_lastPauseThreadId}";
    }

    private static IEnumerable<CallStackFrame> FilterNonSystemModules(
        IReadOnlyList<CallStackFrame> frames,
        IReadOnlyList<ModuleDescriptor> modules)
    {
        foreach (CallStackFrame frame in frames)
        {
            if (!frame.IsPointerIntoModule)
            {
                continue;
            }

            ModuleDescriptor? module = ModuleCatalog.FindByAddress(modules, frame.Address);
            if (module is not null && !CallStackWalker.IsSystemModule(module))
            {
                yield return frame;
            }
        }
    }

    [RelayCommand]
    private async Task ScanStringReferencesAsync()
    {
        if (!_session.Target.IsOpen)
        {
            StatusText = "请先打开目标进程。";
            return;
        }

        try
        {
            IReadOnlyList<StringEntry> entries = await Task.Run(
                    () => _session.ScanStrings(new StringScanOptions
                    {
                        IncludeImageMemory = _settings.Current.SearchMemImage,
                        IncludePrivateMemory = _settings.Current.SearchMemPrivate,
                        IncludeMappedMemory = _settings.Current.SearchMemMapped,
                        ScanUtf16Le = IncludeUnicodeInStringScan
                    }),
                    CancellationToken.None)
                .ConfigureAwait(true);

            StringReferences.Clear();
            foreach (StringEntry entry in entries)
            {
                StringReferences.Add(entry);
            }

            StatusText = $"已扫描 {StringReferences.Count:N0} 个字符串。";
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }
    }

    partial void OnSelectedStringChanged(StringEntry? value)
    {
        SelectedStringReferences.Clear();
        if (value is null)
        {
            return;
        }

        foreach (ulong address in value.References)
        {
            SelectedStringReferences.Add(address);
        }
    }

    [RelayCommand]
    private async Task BuildCrossReferencesAsync()
    {
        if (!_session.Target.IsOpen)
        {
            StatusText = "请先打开目标进程。";
            return;
        }

        try
        {
            XrefDatabase database = await Task.Run(
                    () => _session.BuildCrossReferences(),
                    CancellationToken.None)
                .ConfigureAwait(true);
            CrossReferences.Clear();
            foreach (XrefEntry entry in database.Entries)
            {
                CrossReferences.Add(entry);
            }

            StatusText = $"已建立 {CrossReferences.Count:N0} 条交叉引用。";
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }
    }

    [RelayCommand]
    private async Task OpenSelectedCrossReferenceAsync()
    {
        if (SelectedCrossReference is not null)
        {
            await ShowAddressAsync(SelectedCrossReference.FromAddress);
        }
    }

    [RelayCommand]
    private async Task OpenSelectedAsync()
    {
        if (SelectedProcess is null)
        {
            StatusText = "Select a process first.";
            return;
        }

        try
        {
            StatusText = $"Opening {SelectedProcess.Name} ({SelectedProcess.ProcessId})...";
            bool opened = await _session.OpenAsync(SelectedProcess).ConfigureAwait(true);
            StatusText = opened ? "Opened." : "Open failed.";
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }
    }

    [RelayCommand]
    private async Task AttachSelectedAsync()
    {
        if (SelectedProcess is null)
        {
            StatusText = "Select a process first.";
            return;
        }

        try
        {
            StatusText = $"Attaching to {SelectedProcess.Name} ({SelectedProcess.ProcessId})...";
            bool attached = await _session.AttachAsync(SelectedProcess).ConfigureAwait(true);
            StatusText = attached ? "Attached." : "Attach failed.";
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }
    }

    [RelayCommand]
    private async Task LaunchAsync()
    {
        if (string.IsNullOrWhiteSpace(LaunchCommandLine))
        {
            StatusText = "Enter a command line to launch.";
            return;
        }

        try
        {
            StatusText = "Launching...";
            bool launched = await _session.LaunchAsync(LaunchCommandLine).ConfigureAwait(true);
            StatusText = launched ? "Launched." : "Launch failed.";
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanContinue))]
    private async Task ContinueAsync()
    {
        if (_session.Target.IsOpen && !_session.Debugger.IsDebugging)
        {
            ProcessDescriptor process = SelectedProcess is { } selected &&
                                        selected.ProcessId == _session.Target.ProcessId
                ? selected
                : new ProcessDescriptor(
                    _session.Target.ProcessId,
                    _session.Target.ProcessName,
                    _session.Target.FilePath,
                    0,
                    null);

            try
            {
                bool attached = await _session.AttachAsync(process).ConfigureAwait(true);
                StatusText = attached ? "Attached." : "Attach failed.";
            }
            catch (Exception exception)
            {
                StatusText = exception.Message;
            }

            return;
        }

        await ExecuteDebuggerCommandAsync(_session.Debugger.ContinueAsync).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanPause))]
    private async Task BreakAsync()
    {
        await ExecuteDebuggerCommandAsync(_session.Debugger.BreakAsync).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanStep))]
    private async Task StepIntoAsync()
    {
        await ExecuteDebuggerCommandAsync(_session.Debugger.StepIntoAsync).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanStep))]
    private async Task StepOverAsync()
    {
        await ExecuteDebuggerCommandAsync(_session.Debugger.StepOverAsync).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanStep))]
    private async Task StepOutAsync()
    {
        await ExecuteDebuggerCommandAsync(_session.Debugger.StepOutAsync).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanDetach))]
    private async Task DetachAsync()
    {
        try
        {
            await _session.Debugger.DetachAsync(CancellationToken.None).ConfigureAwait(true);
            StatusText = "Detached.";
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanPassException))]
    private async Task PassExceptionAsync()
    {
        if (LastPauseReason != DebuggerPauseReason.Exception)
        {
            StatusText = "The debugger is not paused on an exception.";
            return;
        }

        await ExecuteDebuggerCommandAsync(_session.Debugger.ContinueAsync).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRunToCursor))]
    private async Task RunToCursorAsync()
    {
        ulong address = SelectedInstruction?.Address ?? 0;
        if (address == 0 || !_session.Target.IsOpen)
        {
            StatusText = "Select an instruction before running to the cursor.";
            return;
        }

        try
        {
            if (!_session.Breakpoints.Contains(address) &&
                !_session.AddBreakpoint(address, temporary: true))
            {
                StatusText = "Could not create a temporary breakpoint at the selected instruction.";
                return;
            }

            bool continued = await _session.Debugger.ContinueAsync(CancellationToken.None)
                .ConfigureAwait(true);
            StatusText = continued
                ? $"Running to 0x{address:X}."
                : "The debugger could not run to the selected instruction.";
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private async Task RestartAsync()
    {
        if (string.IsNullOrWhiteSpace(LaunchCommandLine))
        {
            StatusText = "No launch command is available for restart.";
            return;
        }

        try
        {
            if (_session.Debugger.IsDebugging)
            {
                await _session.Debugger.DetachAsync(CancellationToken.None).ConfigureAwait(true);
            }

            bool launched = await _session.LaunchAsync(LaunchCommandLine).ConfigureAwait(true);
            StatusText = launched ? "Restarted." : "Restart failed.";
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }
    }

    [RelayCommand]
    private async Task NavigateAsync()
    {
        if (!TryResolveAddress(AddressInput, out ulong address, out string error))
        {
            StatusText = error;
            return;
        }

        await ShowAddressAsync(address).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchPattern))
        {
            StatusText = "Enter an AOB pattern.";
            return;
        }

        try
        {
            SearchResult result = await Task.Run(() =>
                _session.Search(new SearchRequest
                {
                    Pattern = SearchPattern,
                    SearchPrivateMemory = _settings.Current.SearchMemPrivate,
                    SearchImageMemory = _settings.Current.SearchMemImage,
                    SearchMappedMemory = _settings.Current.SearchMemMapped
                })).ConfigureAwait(true);

            SearchMatches.Clear();
            foreach (SearchMatch match in result.Matches)
            {
                SearchMatches.Add(match);
            }

            SearchResultCount = SearchMatches.Count;
            StatusText = result.Truncated
                ? $"Search truncated at {SearchResultCount} results."
                : $"Found {SearchResultCount} results.";
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunLua))]
    private async Task RunLuaAsync()
    {
        if (string.IsNullOrWhiteSpace(LuaScriptText))
        {
            LuaOutputText = "脚本为空。";
            return;
        }

        IsLuaRunning = true;
        LuaOutputText = string.Empty;
        try
        {
            LuaScriptExecutionResult result = await _luaHost
                .StartAsync(LuaScriptText)
                .ConfigureAwait(true);
            AppendLuaResult(result);
            if (!result.Succeeded)
            {
                IsLuaRunning = false;
            }
        }
        catch (Exception exception)
        {
            LuaOutputText = exception.Message;
            IsLuaRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopLua))]
    private async Task StopLuaAsync()
    {
        try
        {
            LuaScriptExecutionResult result = await _luaHost
                .StopAsync()
                .ConfigureAwait(true);
            AppendLuaResult(result);
        }
        catch (Exception exception)
        {
            LuaOutputText = exception.Message;
        }
        finally
        {
            IsLuaRunning = false;
        }
    }

    public async Task RunLuaSelectionAsync(string? selection)
    {
        string source = string.IsNullOrWhiteSpace(selection)
            ? LuaScriptText
            : selection;
        if (string.IsNullOrWhiteSpace(source))
        {
            LuaStatusText = "选区为空。";
            return;
        }

        IsLuaRunning = true;
        LuaOutputText = string.Empty;
        try
        {
            LuaScriptExecutionResult result = await _luaHost
                .ExecuteAsync(source)
                .ConfigureAwait(true);
            AppendLuaResult(result);
            LuaStatusText = result.Succeeded
                ? "选区执行完成。"
                : "选区执行失败。";
        }
        catch (Exception exception)
        {
            LuaOutputText = exception.Message;
            LuaStatusText = "选区执行失败。";
        }
        finally
        {
            IsLuaRunning = false;
        }
    }

    [RelayCommand]
    private void CheckLua()
    {
        try
        {
            Script script = new();
            script.LoadString(LuaScriptText);
            LuaProblemsText = string.Empty;
            LuaProblemCount = 0;
            LuaStatusText = "语法检查通过。";
        }
        catch (SyntaxErrorException exception)
        {
            LuaProblemsText = exception.DecoratedMessage;
            LuaProblemCount = 1;
            LuaStatusText = "语法检查发现问题。";
        }
        catch (Exception exception)
        {
            LuaProblemsText = exception.Message;
            LuaProblemCount = 1;
            LuaStatusText = "语法检查发现问题。";
        }
    }

    [RelayCommand]
    private async Task OpenLuaAsync()
    {
        Microsoft.Win32.OpenFileDialog dialog = new()
        {
            Title = "打开 Lua 脚本",
            Filter = "Lua 脚本 (*.lua)|*.lua|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            LuaScriptText = await File.ReadAllTextAsync(dialog.FileName)
                .ConfigureAwait(true);
            LuaScriptPath = dialog.FileName;
            LuaProblemsText = string.Empty;
            LuaProblemCount = 0;
            LuaStatusText = $"已打开 {Path.GetFileName(dialog.FileName)}。";
        }
        catch (Exception exception)
        {
            LuaStatusText = "打开失败：" + exception.Message;
        }
    }

    [RelayCommand]
    private async Task SaveLuaAsync()
    {
        if (string.IsNullOrWhiteSpace(LuaScriptPath) ||
            string.Equals(
                LuaScriptPath,
                "editor.lua",
                StringComparison.OrdinalIgnoreCase))
        {
            await SaveLuaAsAsync().ConfigureAwait(true);
            return;
        }

        await WriteLuaScriptAsync(LuaScriptPath).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SaveLuaAsAsync()
    {
        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            Title = "另存为 Lua 脚本",
            Filter = "Lua 脚本 (*.lua)|*.lua|所有文件 (*.*)|*.*",
            DefaultExt = ".lua",
            AddExtension = true,
            FileName = Path.GetFileName(LuaScriptPath)
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        LuaScriptPath = dialog.FileName;
        await WriteLuaScriptAsync(dialog.FileName).ConfigureAwait(true);
    }

    private async Task WriteLuaScriptAsync(string path)
    {
        try
        {
            await File.WriteAllTextAsync(
                    path,
                    LuaScriptText ?? string.Empty,
                    new UTF8Encoding(false))
                .ConfigureAwait(true);
            LuaStatusText = $"已保存 {Path.GetFileName(path)}。";
        }
        catch (Exception exception)
        {
            LuaStatusText = "保存失败：" + exception.Message;
        }
    }

    [RelayCommand]
    private async Task ResetLuaAsync()
    {
        try
        {
            if (IsLuaRunning)
            {
                await _luaHost.StopAsync().ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            LuaOutputText = exception.Message;
        }
        finally
        {
            IsLuaRunning = false;
            LuaOutputText = string.Empty;
            LuaProblemsText = string.Empty;
            LuaProblemCount = 0;
            LuaAiPreview = string.Empty;
            LuaStatusText = "Lua 引擎已重置。";
        }
    }

    [RelayCommand]
    private void ClearLua()
    {
        LuaScriptText = string.Empty;
        LuaOutputText = string.Empty;
        LuaProblemsText = string.Empty;
        LuaProblemCount = 0;
        LuaStatusText = "已清空。";
    }

    [RelayCommand]
    private async Task GenerateLuaPreviewAsync()
    {
        if (string.IsNullOrWhiteSpace(LuaAiRequirement))
        {
            LuaAiStatusText = "请输入生成需求。";
            return;
        }

        AppSettings settings = _settings.Current;
        if (string.IsNullOrWhiteSpace(settings.AiApiUrl))
        {
            LuaAiStatusText =
                "AI is not configured. Please set API URL and API Key in Settings.";
            return;
        }

        IsLuaAiBusy = true;
        LuaAiStatusText = "正在生成...";
        try
        {
            AiChatClient client = new(settings);
            LuaAiPreview = await client.SendChatAsync(
                    LuaAiRequirement,
                    "你是 DogeDebugger 的 Lua 脚本生成器。根据需求输出完整 Lua 脚本，" +
                    "必须包含 OnStart 和 OnEnd，只输出 Lua 代码。",
                    settings.AiDefaultModel)
                .ConfigureAwait(true);
            LuaAiStatusText = "生成完成。";
        }
        catch (Exception exception)
        {
            LuaAiStatusText = "生成失败：" + exception.Message;
        }
        finally
        {
            IsLuaAiBusy = false;
        }
    }

    public void UpdateLuaCaret(int line, int column)
    {
        LuaCaretLine = Math.Max(1, line);
        LuaCaretColumn = Math.Max(1, column);
    }

    [RelayCommand(CanExecute = nameof(CanToggleBreakpoint))]
    private async Task ToggleBreakpointAsync()
    {
        try
        {
            ulong address = SelectedInstruction?.Address ??
                            (_session.Target.IsOpen
                                ? _session.Debugger.CurrentInstructionPointer
                                : 0);
            if (address == 0)
            {
                StatusText = "Could not toggle a breakpoint at the selected address.";
                return;
            }

            bool changed = _session.Breakpoints.Contains(address)
                ? _session.RemoveBreakpoint(address)
                : _session.AddBreakpoint(address);
            if (!changed)
            {
                StatusText = "Could not toggle a breakpoint at the selected address.";
                return;
            }

            RefreshBreakpoints();
            StatusText = _session.Breakpoints.Contains(address)
                ? $"Breakpoint set at 0x{address:X}."
                : $"Breakpoint removed at 0x{address:X}.";
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }

        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task RemoveBreakpointAsync()
    {
        try
        {
            ulong address = SelectedInstruction?.Address ??
                            (_session.Target.IsOpen
                                ? _session.Debugger.CurrentInstructionPointer
                                : 0);
            if (address == 0 || !_session.RemoveBreakpoint(address))
            {
                StatusText = "No breakpoint was removed.";
                return;
            }

            RefreshBreakpoints();
            StatusText = $"Breakpoint removed at 0x{address:X}.";
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }

        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task OpenSelectedModuleAsync()
    {
        if (SelectedModule is not null)
        {
            await ShowAddressAsync(SelectedModule.EntryPoint != 0
                    ? SelectedModule.EntryPoint
                    : SelectedModule.BaseAddress)
                .ConfigureAwait(true);
        }
    }

    partial void OnSelectedModuleChanged(ModuleDescriptor? value)
    {
        _ = LoadSelectedModuleMetadataAsync(value);
    }

    private async Task LoadSelectedModuleMetadataAsync(ModuleDescriptor? module)
    {
        SelectedModuleImports.Clear();
        SelectedModuleExports.Clear();
        SelectedModuleMetadata = null;
        if (module is null ||
            string.IsNullOrWhiteSpace(module.FilePath) ||
            !File.Exists(module.FilePath))
        {
            return;
        }

        try
        {
            PeModuleMetadata metadata = await Task.Run(
                    () => _session.AnalyzeModule(module),
                    CancellationToken.None)
                .ConfigureAwait(true);
            SelectedModuleMetadata = metadata;
            foreach (PeImport import in metadata.Imports)
            {
                SelectedModuleImports.Add(new ModuleImportRow(
                    import.DisplayAddress(module.BaseAddress),
                    import.ModuleName,
                    import.FunctionDisplayName));
            }

            foreach (PeExport export in metadata.Exports)
            {
                SelectedModuleExports.Add(new ModuleExportRow(
                    export.DisplayAddress(module.BaseAddress),
                    export.OrdinalText,
                    export.ForwarderName ?? export.Name));
            }
        }
        catch (Exception exception)
        {
            AppendLog($"模块解析失败: {module.Name}: {exception.Message}");
        }
    }

    [RelayCommand]
    private async Task RefreshExceptionHandlersAsync()
    {
        if (!_session.Target.IsOpen)
        {
            ExceptionHandlerStatusText = "请先打开目标进程。";
            IsExceptionHandlerScanning = false;
            HasExceptionHandlerResult = false;
            return;
        }

        IsExceptionHandlerScanning = true;
        HasExceptionHandlerResult = false;
        try
        {
            ExceptionHandlerScanResult result = await Task.Run(
                    () => _session.ScanExceptionHandlers(),
                    CancellationToken.None)
                .ConfigureAwait(true);

            VehEntries.Clear();
            PdataEntries.Clear();
            HookEntries.Clear();
            foreach (VehEntry entry in result.VehEntries)
            {
                VehEntries.Add(entry);
            }

            foreach (PdataSehEntry entry in result.PdataEntries)
            {
                PdataEntries.Add(entry);
            }

            foreach (HookDetectionEntry entry in result.HookEntries)
            {
                HookEntries.Add(entry);
            }

            ExceptionHandlerStatusText = result.PdataTruncated
                ? $"VEH {VehEntries.Count}，SEH {PdataEntries.Count}/{result.PdataCandidateCount}（结果已截断），Hook {HookEntries.Count}。"
                : $"VEH {VehEntries.Count}，SEH {PdataEntries.Count}，Hook {HookEntries.Count}。";
            HasExceptionHandlerResult = true;
        }
        catch (Exception exception)
        {
            ExceptionHandlerStatusText = exception.Message;
        }
        finally
        {
            IsExceptionHandlerScanning = false;
        }
    }

    [RelayCommand]
    private async Task OpenSelectedRegionAsync()
    {
        if (SelectedMemoryRegion is not null)
        {
            await ShowAddressAsync(SelectedMemoryRegion.BaseAddress).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task OpenSelectedSearchResultAsync()
    {
        SearchMatch? match = SearchMatches.FirstOrDefault();
        if (match is not null)
        {
            await ShowAddressAsync(match.Address).ConfigureAwait(true);
        }
    }

    private async Task ExecuteDebuggerCommandAsync(
        Func<CancellationToken, Task<bool>> command)
    {
        try
        {
            bool succeeded = await command(CancellationToken.None).ConfigureAwait(true);
            if (!succeeded)
            {
                StatusText = "The debugger command could not be completed.";
            }
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }
    }

    private bool CanContinue() =>
        _session.Target.IsOpen &&
        (!_session.Debugger.IsDebugging || _session.Debugger.IsPaused);

    private bool CanPause() => _session.Debugger.IsDebugging && !_session.Debugger.IsPaused;

    private bool CanStep() => _session.Debugger.IsDebugging && _session.Debugger.IsPaused;

    private bool CanDetach() => _session.Debugger.IsDebugging;

    private bool CanPassException() => LastPauseReason == DebuggerPauseReason.Exception;

    private bool CanRunToCursor() =>
        _session.Debugger.IsDebugging &&
        _session.Debugger.IsPaused &&
        SelectedInstruction is not null &&
        SelectedInstruction.Address != 0;

    private bool CanRestart() => !string.IsNullOrWhiteSpace(LaunchCommandLine);

    private bool CanToggleBreakpoint() =>
        _session.Target.IsOpen && SelectedInstruction is not null && SelectedInstruction.Address != 0;

    private bool CanRunLua() => !IsLuaRunning;

    private bool CanStopLua() => IsLuaRunning;

    private void NotifyDebuggerCommandStates()
    {
        ContinueCommand.NotifyCanExecuteChanged();
        BreakCommand.NotifyCanExecuteChanged();
        StepIntoCommand.NotifyCanExecuteChanged();
        StepOverCommand.NotifyCanExecuteChanged();
        StepOutCommand.NotifyCanExecuteChanged();
        DetachCommand.NotifyCanExecuteChanged();
        PassExceptionCommand.NotifyCanExecuteChanged();
        RunToCursorCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
        ToggleBreakpointCommand.NotifyCanExecuteChanged();
    }

    private async Task ShowAddressAsync(
        ulong address,
        bool recordHistory = true)
    {
        try
        {
            Disassembly.Clear();
            foreach (InstructionSnapshot instruction in _session.Disassemble(address, 80))
            {
                Disassembly.Add(instruction);
            }

            SelectedInstruction = Disassembly.FirstOrDefault();
            AddressInput = $"0x{address:X}";
            RefreshHex(address);
            if (recordHistory && _currentAddress != address)
            {
                if (_currentAddress is { } previousAddress)
                {
                    _backAddressHistory.Push(previousAddress);
                }

                _forwardAddressHistory.Clear();
            }

            _currentAddress = address;
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoForward));
            GoBackCommand.NotifyCanExecuteChanged();
            GoForwardCommand.NotifyCanExecuteChanged();
            StatusText = $"Navigated to 0x{address:X}.";
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }

        await Task.CompletedTask;
    }

    private void HandlePaused(object? sender, DebuggerPausedEventArgs args)
    {
        if (DispatchToUiIfNeeded(() => HandlePaused(sender, args)))
        {
            return;
        }

        LastPauseReason = args.Reason;
        OnPropertyChanged(nameof(IsDebuggerPaused));
        NotifyDebuggerCommandStates();
        DebuggerStateText = $"Paused: {args.Reason}";
        AppendLog($"暂停：{args.Reason} @ 0x{args.Registers.InstructionPointer:X}");
        RefreshRegisters(args.Registers);
        _lastPauseRegisters = args.Registers;
        _lastPauseThreadId = args.Event.ThreadId;
        SourceDebugPanel.OnPaused(
            args.Registers.InstructionPointer,
            args.Event.ThreadId);
        RebuildCallStack();
        _ = ShowAddressAsync(args.Registers.InstructionPointer);
        RefreshBreakpoints();
    }

    private void HandleStateChanged(object? sender, DebuggerStateChangedEventArgs args)
    {
        if (DispatchToUiIfNeeded(() => HandleStateChanged(sender, args)))
        {
            return;
        }

        if (!args.IsPaused)
        {
            LastPauseReason = null;
        }

        DebuggerStateText = args.IsDebugging
            ? args.IsPaused ? "Paused" : "Running"
            : "Detached";
        OnPropertyChanged(nameof(IsDebuggerPaused));
        if (!args.IsPaused)
        {
            _lastPauseRegisters = null;
            CallStack.Clear();
            CallStackFrameCount = 0;
            CallStackStatusText = args.IsDebugging ? "运行中..." : "等待调试器中断...";
        }

        AppendLog(args.IsDebugging
            ? args.IsPaused ? "调试器已暂停" : "调试器正在运行"
            : "调试器已脱离");
        NotifyDebuggerCommandStates();
    }

    private void HandleDebuggerDiagnostic(string message)
    {
        WriteDiagnostic(message);

        _dispatcher.BeginInvoke(() => AppendLog($"调试诊断: {message}"));
    }

    private static void WriteDiagnostic(string message)
    {
        if (!string.IsNullOrWhiteSpace(DiagnosticLogPath))
        {
            try
            {
                File.AppendAllText(
                    DiagnosticLogPath,
                    $"{DateTime.Now:O} {message}{Environment.NewLine}",
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch
            {
            }
        }
    }

    private void HandleTraceProgressChanged(int current, int total)
    {
        _dispatcher.BeginInvoke(() =>
        {
            TraceProgress = total > 0
                ? Math.Clamp((double)current / total, 0, 1)
                : 0;
            TraceSummaryText = total > 0
                ? $"追踪中: {current:N0} / {total:N0} 步"
                : $"追踪中: {current:N0} 步";
            TraceProgressChanged?.Invoke(current, total);
        });
    }

    private void HandleTraceCompleted(TraceSession session)
    {
        _dispatcher.BeginInvoke(() =>
        {
            CurrentTraceSession = session;
            TraceSteps.Clear();
            foreach (TraceStep step in session.Steps)
            {
                TraceSteps.Add(step);
            }

            IsTraceRunning = false;
            TraceProgress = 1;
            TraceSummaryText = BuildTraceSummary(session);
            OnPropertyChanged(nameof(HasTraceResult));
            AppendLog($"追踪完成: {session.Steps.Count:N0} 步，原因: {session.StopReason}");
            TraceCompleted?.Invoke(session);
        });
    }

    private static string BuildTraceSummary(TraceSession session)
    {
        string reason = session.StopReason switch
        {
            "StepCount" => "达到步数",
            "RipMatch" => "命中目标 RIP",
            "LuaCondition" => "Lua 条件成立",
            "UserCancel" => "用户取消",
            "DebuggerUnavailable" => "调试器不可用",
            "LuaConditionError" => "Lua 条件错误",
            "ForcedStop" => "达到上限",
            _ => session.StopReason
        };
        return $"共 {session.Steps.Count:N0} 步 | {reason}";
    }

    private void HandleTargetChanged(object? sender, EventArgs args)
    {
        WriteDiagnostic(
            $"TargetChanged enter dispatcherAccess={_dispatcher.CheckAccess()} " +
            $"open={_session.Target.IsOpen} pid={_session.Target.ProcessId}");
        if (DispatchToUiIfNeeded(() => HandleTargetChanged(sender, args)))
        {
            return;
        }

        WriteDiagnostic("TargetChanged applying to UI");
        TargetText = _session.Target.IsOpen
            ? $"{_session.Target.ProcessName} ({_session.Target.ProcessId}) - {(_session.Target.Is64Bit ? "x64" : "x86")}"
            : "No target";
        OnPropertyChanged(nameof(IsTargetOpen));
        LoadSymbolsCommand.NotifyCanExecuteChanged();
        CloseProcessHandleCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsTarget64Bit));
        OnPropertyChanged(nameof(TargetProcessId));
        OnPropertyChanged(nameof(TargetProcessName));
        OnPropertyChanged(nameof(TargetProcessPath));
        MemorySearchWorkspace.ResetAll();
        MemorySearchWorkspace.RefreshTargetState();
        RttiPanel.OnTargetChanged();
        MonoPanel.OnTargetChanged();
        UnrealPanel.OnTargetChanged();
        CrossRefPanel.OnTargetChanged();
        SourceDebugPanel.OnTargetChanged();
        ResetAutoAssemblerEngine();
        RefreshPanels();
        NotifyDebuggerCommandStates();
        if (_session.Target.IsOpen)
        {
            _ = InitializeDefaultDisassemblyAsync();
        }
        else
        {
            _currentAddress = null;
            _backAddressHistory.Clear();
            _forwardAddressHistory.Clear();
            Disassembly.Clear();
            HexLines.Clear();
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoForward));
        }
        WriteDiagnostic(
            $"TargetChanged applied target={TargetText} modules={Modules.Count} " +
            $"regions={MemoryRegions.Count} threads={Threads.Count}");
    }

    private void ResetAutoAssemblerEngine()
    {
        if (_session.Target.IsOpen)
        {
            _autoAssemblerSymbolResolver = new AutoAssemblerSymbolResolver(
                _session.Target,
                _session.EnumerateModules);
            _autoAssemblerEngine = new AutoAssemblerEngine(
                _session.Target,
                _autoAssemblerSymbolResolver);
            _callStackWalker = new CallStackWalker(
                _session.Target,
                _session.EnumerateModules);
        }
        else
        {
            _autoAssemblerSymbolResolver = null;
            _autoAssemblerEngine = null;
            _callStackWalker = null;
        }

        ResetCallStack();
        IsAutoAssemblerActive = false;
        AutoAssemblerStatusText = "就绪";
        OnPropertyChanged(nameof(AutoAssemblerOverlayVisibility));
        OnPropertyChanged(nameof(ProcessOverlayVisibility));
    }

    private async Task InitializeDefaultDisassemblyAsync()
    {
        ModuleDescriptor? module = FindPrimaryModule(Modules);
        ulong address = module is null
            ? 0
            : module.EntryPoint != 0
                ? module.EntryPoint
                : module.BaseAddress;
        if (address != 0)
        {
            await ShowAddressAsync(address).ConfigureAwait(true);
        }
    }

    private bool DispatchToUiIfNeeded(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            return false;
        }

        _dispatcher.BeginInvoke(action);
        return true;
    }

    private void RefreshPanels()
    {
        Modules.Clear();
        MemoryRegions.Clear();
        Threads.Clear();
        if (!_session.Target.IsOpen)
        {
            return;
        }

        foreach (ModuleDescriptor module in _session.EnumerateModules())
        {
            Modules.Add(module);
        }

        foreach (MemoryRegionInfo region in _session.EnumerateMemoryRegions())
        {
            MemoryRegions.Add(region);
        }

        foreach (ThreadDescriptor thread in _session.EnumerateThreads())
        {
            Threads.Add(thread);
        }

        Handles.Clear();
        foreach (ProcessHandleEntry handle in _session.EnumerateHandles())
        {
            Handles.Add(handle);
        }

        RefreshBreakpoints();
    }

    private void AppendLog(string message)
    {
        LogMessages.Add(new LogRow(DateTime.Now, message));
        if (LogMessages.Count > 2_000)
        {
            LogMessages.RemoveAt(0);
        }
    }

    private void AppendLuaResult(LuaScriptExecutionResult result)
    {
        if (!string.IsNullOrEmpty(result.OutputText))
        {
            LuaOutputText += result.OutputText;
        }

        if (!result.Succeeded && !string.IsNullOrWhiteSpace(result.ErrorText))
        {
            LuaOutputText += result.ErrorText + Environment.NewLine;
        }
    }

    private void RefreshBreakpoints()
    {
        Breakpoints.Clear();
        if (!_session.Target.IsOpen)
        {
            return;
        }

        foreach (BreakpointEntry breakpoint in _session.Breakpoints.Entries)
        {
            ModuleDescriptor? module = ModuleCatalog.FindByAddress(
                Modules,
                breakpoint.Address);
            breakpoint.ModuleName = module?.Name ?? string.Empty;
            InstructionSnapshot? instruction = _session
                .Disassemble(breakpoint.Address, 1)
                .FirstOrDefault();
            breakpoint.SymbolName = instruction?.Mnemonic ?? string.Empty;
            Breakpoints.Add(breakpoint);
        }
    }

    private void RefreshRegisters(RegisterSnapshot registers)
    {
        Registers.Clear();
        foreach ((string name, ulong value) in registers.GeneralPurposeRegisters)
        {
            Registers.Add(new RegisterRow(name, value));
        }
    }

    private void RefreshHex(ulong address)
    {
        HexLines.Clear();
        ulong start = address & ~0xFUL;
        byte[] bytes = _session.ReadBytes(start, 256);
        for (int offset = 0; offset < bytes.Length; offset += 16)
        {
            int count = Math.Min(16, bytes.Length - offset);
            ReadOnlySpan<byte> row = bytes.AsSpan(offset, count);
            string hex = string.Join(
                " ",
                row.ToArray().Select(static value => value.ToString("X2", CultureInfo.InvariantCulture)));
            string ascii = new(
                row.ToArray()
                    .Select(static value => value is >= 0x20 and <= 0x7E ? (char)value : '.')
                    .ToArray());
            HexLines.Add(new HexViewLine(start + (uint)offset, hex, ascii));
        }
    }

    private bool TryResolveAddress(string expression, out ulong address, out string error)
    {
        address = 0;
        error = string.Empty;
        string value = expression.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Address is empty.";
            return false;
        }

        int plusIndex = value.IndexOf('+');
        if (plusIndex > 0 && !value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            string moduleName = value[..plusIndex].Trim();
            string offsetText = value[(plusIndex + 1)..].Trim();
            ModuleDescriptor? module = Modules.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, moduleName, StringComparison.OrdinalIgnoreCase));
            if (module is null)
            {
                error = $"Module '{moduleName}' was not found.";
                return false;
            }

            if (!TryParseAddress(offsetText, out ulong offset))
            {
                error = $"Invalid module offset: {offsetText}";
                return false;
            }

            address = module.BaseAddress + offset;
            return true;
        }

        if (!TryParseAddress(value, out address))
        {
            error = $"Invalid address: {value}";
            return false;
        }

        return true;
    }

    private static bool TryParseAddress(string text, out ulong address)
    {
        string value = text.Trim();
        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(
                value[2..],
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out address)
            : ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out address) ||
              ulong.TryParse(
                  value,
                  NumberStyles.AllowHexSpecifier,
                  CultureInfo.InvariantCulture,
                  out address);
    }
}

public sealed record HexViewLine(ulong Address, string Hex, string Ascii)
{
    public string AddressText => $"0x{Address:X}";
}

public sealed record RegisterRow(string Name, ulong Value)
{
    public string ValueText => $"0x{Value:X}";
}

public sealed record CallStackRow(
    int Index,
    ulong Address,
    ulong SlotAddress,
    string First,
    string Second,
    string Third,
    ulong PrimaryAddress,
    ulong PointerTarget)
{
    public string AddressText => $"0x{Address:X}";
}

public sealed record CommentRow(ulong Address, string Text)
{
    public string AddressText => $"0x{Address:X}";

    public string ModuleName => string.Empty;
}

public sealed record CustomSymbolRow(ulong Address, string Name)
{
    public string AddressText => $"0x{Address:X}";

    public string ModuleName => string.Empty;
}

public sealed record LogRow(DateTime Timestamp, string Message)
{
    public string TimestampText => Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
}

public sealed record ModuleImportRow(
    string AddressText,
    string ModuleName,
    string FunctionName);

public sealed record ModuleExportRow(
    string AddressText,
    string OrdinalText,
    string Name);
