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
    static MainViewModel()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

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
    private readonly ulong?[] _disassemblyBookmarks = new ulong?[10];
    private readonly Stack<NopUndoRecord> _nopUndoHistory = [];
    private readonly Dictionary<string, ulong> _previousRegisterValues =
        new(StringComparer.OrdinalIgnoreCase);

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
    private AssemblySyntax _disassemblySyntax = AssemblySyntax.Intel;

    [ObservableProperty]
    private AssemblyAddressMode _disassemblyAddressMode =
        AssemblyAddressMode.ModuleOffset;

    [ObservableProperty]
    private DisassemblyBytesStyle _disassemblyBytesStyle =
        DisassemblyBytesStyle.X64Dbg;

    [ObservableProperty]
    private bool _showDisassemblyBytes = true;

    [ObservableProperty]
    private bool _useSignedImmediateOperands;

    [ObservableProperty]
    private bool _showJumpArrows = true;

    [ObservableProperty]
    private HexDisplayMode _hexDisplayMode = HexDisplayMode.Hex;

    [ObservableProperty]
    private int _hexBytesPerRow = 16;

    [ObservableProperty]
    private string _hexTextEncoding = "ASCII";

    [ObservableProperty]
    private bool _registerDecimalDisplay;

    [ObservableProperty]
    private bool _highlightChangedRegisters = true;

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

    public string WindowTitle
    {
        get
        {
            const string productTitle = "DogeDebugger v5.0.0（本地免登录）";
            if (!_session.Target.IsOpen)
            {
                return productTitle;
            }

            return
                $"{productTitle} - {_session.Target.ProcessName} " +
                $"(PID: {_session.Target.ProcessId}) " +
                $"[{(_session.Target.Is64Bit ? "x64" : "x86")}]";
        }
    }

    public string CommittedMemoryText
    {
        get
        {
            ulong committed = MemoryRegions
                .Where(static region => region.State == 0x1000)
                .Aggregate(
                    0UL,
                    static (total, region) =>
                        total > ulong.MaxValue - region.Size
                            ? ulong.MaxValue
                            : total + region.Size);
            return $"已提交:  {committed / 1024d / 1024d:N1} MB";
        }
    }

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
    private string _exceptionHandlerStatusText = "VEH:  0   .pdata:  0";

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
        _session.Breakpoints.BreakpointChanged += HandleBreakpointChanged;
        _session.HardwareBreakpoints.BreakpointChanged += HandleBreakpointChanged;
        _session.Debugger.HardwareBreakpointHit += HandleHardwareBreakpointHit;
        _mcpServer.Diagnostic += (_, message) => McpStateText = message;
        NotesText = _settings.Current.GlobalNotes;
        DisassemblyBytesStyle = (DisassemblyBytesStyle)Math.Clamp(
            _settings.Current.DisassemblyBytesStyle,
            (int)DogeDebugger.Core.Disassembly.DisassemblyBytesStyle.X64Dbg,
            (int)DogeDebugger.Core.Disassembly.DisassemblyBytesStyle.CheatEngine);
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
        InstructionSearchPanel = new InstructionSearchViewModel(
            session,
            address => _ = ShowAddressAsync(address));
        SourceDebugPanel = new SourceDebugPanelViewModel(session, settings);
    }

    internal SettingsStore Settings => _settings;

    public DebuggerSession Session => _session;

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

    public ObservableCollection<HardwareAccessRecord> HardwareAccessRecords { get; } = [];

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

    public InstructionSearchViewModel InstructionSearchPanel { get; }

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

    public string StatusLabelText =>
        !_session.Target.IsOpen
            ? "就绪"
            : _session.Debugger.IsDebugging
                ? _session.Debugger.IsPaused ? "已暂停" : "已附加"
                : "已打开";

    public string StatusDescriptionText =>
        _session.Target.IsOpen && !_session.Debugger.IsPaused
            ? $"PID: {_session.Target.ProcessId} ({_session.Target.ProcessName})"
            : StatusText;

    public string HexBytesHeader =>
        string.Join(
            ' ',
            Enumerable.Range(0, HexBytesPerRow)
                .Select(static value => value.ToString("X2")));

    public TraceSession? CurrentTraceSession { get; private set; }

    public bool HasTraceResult => CurrentTraceSession is not null;

    public event Action<int, int>? TraceProgressChanged;

    public event Action<TraceSession>? TraceCompleted;

    public async Task InitializeAsync()
    {
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

        await RefreshProcessesAsync().ConfigureAwait(true);
        string pluginDirectory = Path.Combine(AppContext.BaseDirectory, "Plugins");
        Dictionary<string, bool> pluginStates = _settings.Current.PluginStates.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.IsEnabled,
            StringComparer.OrdinalIgnoreCase);
        await _pluginRuntime.LoadDirectoryAsync(pluginDirectory, pluginStates)
            .ConfigureAwait(true);
        LoadedPluginCount = _pluginRuntime.Plugins.Count;
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

    public ulong SelectedInstructionAddress =>
        SelectedInstruction?.Address ?? 0;

    public bool HasSelectedInstruction =>
        SelectedInstruction is not null && SelectedInstruction.Address != 0;

    public bool CanSetSelectedInstructionPointer =>
        HasSelectedInstruction &&
        _session.Debugger.IsDebugging &&
        _session.Debugger.IsPaused;

    public bool HasBreakpointAtSelectedAddress =>
        HasSelectedInstruction &&
        (_session.Breakpoints.Contains(SelectedInstructionAddress) ||
         _session.HardwareBreakpoints.Find(SelectedInstructionAddress) is not null);

    public bool IsSelectedInstructionNop =>
        SelectedInstruction is { Mnemonic: "nop" };

    public bool CanUndoNopFill => _nopUndoHistory.Count > 0;

    public string InstructionPointerName =>
        _session.Target.Is64Bit ? "RIP" : "EIP";

    public async Task NavigateToAddressExpressionAsync(string expression)
    {
        if (!TryResolveAddress(expression, out ulong address, out string error))
        {
            StatusText = error;
            return;
        }

        await ShowAddressAsync(address).ConfigureAwait(true);
    }

    public async Task FollowSelectedBranchAsync()
    {
        InstructionSnapshot? instruction = SelectedInstruction;
        if (instruction is null)
        {
            return;
        }

        ulong target = instruction.NearBranchTarget ?? 0;
        if (target == 0 && instruction.AbsoluteMemoryAddresses.Count > 0)
        {
            int pointerSize = _session.Target.Is64Bit ? sizeof(ulong) : sizeof(uint);
            foreach (ulong address in instruction.AbsoluteMemoryAddresses)
            {
                byte[] bytes = _session.ReadBytes(address, pointerSize);
                if (bytes.Length != pointerSize)
                {
                    continue;
                }

                target = pointerSize == sizeof(ulong)
                    ? BitConverter.ToUInt64(bytes)
                    : BitConverter.ToUInt32(bytes);
                if (target != 0)
                {
                    break;
                }
            }
        }

        if (target != 0)
        {
            await ShowAddressAsync(target).ConfigureAwait(true);
        }
    }

    public void SetSelectedInstructionPointer()
    {
        if (!CanSetSelectedInstructionPointer ||
            !_session.Debugger.SetCurrentInstructionPointer(SelectedInstructionAddress))
        {
            StatusText = $"目标未暂停，无法修改{InstructionPointerName}。";
            return;
        }

        StatusText = $"{InstructionPointerName} 已设置为 0x{SelectedInstructionAddress:X}。";
    }

    public bool ToggleBreakpointAtSelected()
    {
        if (!HasSelectedInstruction)
        {
            return false;
        }

        bool changed;
        if (_session.HardwareBreakpoints.Find(SelectedInstructionAddress) is not null)
        {
            changed = _session.HardwareBreakpoints.Remove(SelectedInstructionAddress);
        }
        else if (_session.Breakpoints.Contains(SelectedInstructionAddress))
        {
            changed = _session.RemoveBreakpoint(SelectedInstructionAddress);
        }
        else
        {
            changed = _session.AddBreakpoint(SelectedInstructionAddress);
        }
        if (changed)
        {
            RefreshBreakpoints();
            RefreshSelectedDisassemblyMetadata();
        }

        return changed;
    }

    public bool DeleteBreakpointAtSelected()
    {
        if (!HasSelectedInstruction)
        {
            return false;
        }

        bool changed = _session.Breakpoints.Contains(SelectedInstructionAddress)
            ? _session.RemoveBreakpoint(SelectedInstructionAddress)
            : _session.HardwareBreakpoints.Remove(SelectedInstructionAddress);
        if (changed)
        {
            RefreshBreakpoints();
            RefreshSelectedDisassemblyMetadata();
        }

        return changed;
    }

    public string? ValidateBreakpointCondition(string condition) =>
        _session.Debugger.ValidateBreakpointCondition(condition);

    public void UpdateBreakpointCondition(ulong address, string? condition)
    {
        bool changed = _session.Breakpoints.Contains(address)
            ? _session.Breakpoints.UpdateCondition(address, condition)
            : _session.HardwareBreakpoints.UpdateCondition(address, condition);
        if (changed)
        {
            RefreshBreakpoints();
        }
    }

    public void ClearBreakpointCondition(ulong address) =>
        UpdateBreakpointCondition(address, null);

    public string GetBreakpointCondition(ulong address) =>
        _session.Breakpoints.Find(address)?.Condition ??
        _session.HardwareBreakpoints.Find(address)?.Condition ??
        string.Empty;

    public bool AddHardwareBreakpoint(
        BreakpointKind kind,
        string? condition = null)
    {
        if (!HasSelectedInstruction ||
            !_session.HardwareBreakpoints.Add(
                SelectedInstructionAddress,
                kind,
                condition))
        {
            StatusText = "无法设置硬件断点（最多 4 个，或地址无效）。";
            return false;
        }

        RefreshBreakpoints();
        StatusText = $"硬件断点已设置 @ 0x{SelectedInstructionAddress:X}。";
        RefreshSelectedDisassemblyMetadata();
        return true;
    }

    public bool RemoveBreakpoint(ulong address)
    {
        bool changed = _session.Breakpoints.Remove(address);
        if (changed)
        {
            RefreshBreakpoints();
            RefreshSelectedDisassemblyMetadata();
        }

        return changed;
    }

    public bool RemoveHardwareBreakpoint(ulong address)
    {
        bool changed = _session.HardwareBreakpoints.Remove(address);
        if (changed)
        {
            RefreshBreakpoints();
            RefreshSelectedDisassemblyMetadata();
        }

        return changed;
    }

    public void ClearAllBreakpoints()
    {
        _session.Breakpoints.Clear();
        _session.HardwareBreakpoints.Clear();
        RefreshBreakpoints();
        RefreshSelectedDisassemblyMetadata();
    }

    public async Task NopFillSelectedAsync()
    {
        InstructionSnapshot? instruction = SelectedInstruction;
        if (instruction is null || instruction.Bytes.Length == 0)
        {
            return;
        }

        BreakpointEntry? breakpoint = _session.Breakpoints.Find(instruction.Address);
        byte[] originalBytes = instruction.Bytes.ToArray();
        if (breakpoint is not null && originalBytes.Length > 0)
        {
            originalBytes[0] = breakpoint.OriginalByte;
        }

        if (breakpoint is not null)
        {
            _session.Breakpoints.Remove(breakpoint.Address);
        }

        byte[] replacement = Enumerable.Repeat((byte)0x90, instruction.Bytes.Length).ToArray();
        bool written = _session.WriteBytes(instruction.Address, replacement);
        if (written)
        {
            _nopUndoHistory.Push(new NopUndoRecord(
                instruction.Address,
                originalBytes));
            OnPropertyChanged(nameof(CanUndoNopFill));
        }

        if (written)
        {
            await ShowAddressAsync(instruction.Address, recordHistory: false)
                .ConfigureAwait(true);
            StatusText = $"NOP 填充完成 @ 0x{instruction.Address:X}。";
        }
        else
        {
            StatusText = "NOP 填充失败。";
        }

        if (breakpoint is not null)
        {
            _session.Breakpoints.Add(breakpoint.Address);
        }
    }

    public async Task UndoNopFillAsync()
    {
        if (!_nopUndoHistory.TryPop(out NopUndoRecord record))
        {
            return;
        }

        OnPropertyChanged(nameof(CanUndoNopFill));
        if (!_session.WriteBytes(record.Address, record.OriginalBytes))
        {
            StatusText = "撤销 NOP 填充失败。";
            return;
        }

        await ShowAddressAsync(record.Address, recordHistory: false)
            .ConfigureAwait(true);
        StatusText = $"已撤销 NOP 填充 @ 0x{record.Address:X}。";
    }

    public async Task<(bool Success, string Message)> AssembleSelectedInstructionAsync(
        string assemblyText)
    {
        InstructionSnapshot? instruction = SelectedInstruction;
        if (instruction is null || string.IsNullOrWhiteSpace(assemblyText))
        {
            return (false, "请选择一条反汇编指令并输入汇编文本。");
        }

        try
        {
            byte[] encoded = AutoAssemblerTextAssembler.Assemble(
                assemblyText,
                instruction.Address,
                _session.Target.Is64Bit);
            if (encoded.Length > instruction.Length)
            {
                return (
                    false,
                    $"新指令需要 {encoded.Length} 字节，超过原指令的 {instruction.Length} 字节。");
            }

            byte[] replacement = Enumerable.Repeat((byte)0x90, instruction.Length).ToArray();
            Array.Copy(encoded, replacement, encoded.Length);
            BreakpointEntry? breakpoint = _session.Breakpoints.Find(instruction.Address);
            if (breakpoint is not null)
            {
                _session.Breakpoints.Remove(breakpoint.Address);
            }

            bool written = _session.WriteBytes(instruction.Address, replacement);
            if (!written)
            {
                return (false, "写入汇编结果失败。");
            }

            await ShowAddressAsync(instruction.Address, recordHistory: false)
                .ConfigureAwait(true);
            if (breakpoint is not null)
            {
                _session.Breakpoints.Add(breakpoint.Address);
            }

            StatusText = $"已修改汇编指令 @ 0x{instruction.Address:X}。";
            return (true, string.Empty);
        }
        catch (Exception exception)
        {
            return (false, exception.Message);
        }
    }

    public void SetInstructionComment(ulong address, string? comment)
    {
        foreach (CommentRow row in Comments
                     .Where(row => row.Address == address)
                     .ToArray())
        {
            Comments.Remove(row);
        }

        if (!string.IsNullOrWhiteSpace(comment))
        {
            Comments.Add(new CommentRow(address, comment.Trim()));
        }

        RefreshSelectedDisassemblyMetadata();
    }

    public string GetInstructionComment(ulong address) =>
        Comments.FirstOrDefault(row => row.Address == address)?.Text ?? string.Empty;

    public void SetInstructionCustomSymbol(ulong address, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            RemoveInstructionCustomSymbol(address);
            return;
        }

        RegisterCustomSymbol(address, name.Trim());
        RefreshBreakpoints();
        RefreshSelectedDisassemblyMetadata();
    }

    public void RemoveInstructionCustomSymbol(ulong address)
    {
        foreach (CustomSymbolRow row in CustomSymbols
                     .Where(row => row.Address == address)
                     .ToArray())
        {
            CustomSymbols.Remove(row);
        }

        RefreshSelectedDisassemblyMetadata();
        RefreshBreakpoints();
    }

    public string GetInstructionCustomSymbol(ulong address) =>
        CustomSymbols.FirstOrDefault(row => row.Address == address)?.Name ?? string.Empty;

    public AobSignature? GenerateSignatureForInstructions(
        IReadOnlyList<InstructionSnapshot> instructions)
    {
        if (instructions.Count == 0)
        {
            return null;
        }

        List<byte> bytes = [];
        List<bool> mask = [];
        foreach (InstructionSnapshot instruction in instructions)
        {
            bytes.AddRange(instruction.Bytes);
            mask.AddRange(instruction.FixedByteMask.Select(
                static value => value != 0));
        }

        while (mask.Count > 0 && !mask[^1])
        {
            mask.RemoveAt(mask.Count - 1);
            bytes.RemoveAt(bytes.Count - 1);
        }

        if (bytes.Count == 0)
        {
            return null;
        }

        string pattern = string.Join(
            ' ',
            bytes.Select((value, index) =>
                mask[index] ? value.ToString("X2") : "??"));
        AobSignature signature = new()
        {
            Pattern = pattern,
            Bytes = bytes.ToArray(),
            Mask = mask.ToArray(),
            MatchCount = 0
        };
        StatusText = $"已生成特征码：{signature.Pattern}";
        return signature;
    }

    public Task<AobSignature?> GenerateSelectedSignatureAsync() =>
        Task.FromResult(GenerateSignatureForInstructions(
            SelectedInstruction is null ? [] : [SelectedInstruction]));

    public async Task<bool> SearchInstructionTextAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || !HasSelectedInstruction)
        {
            return false;
        }

        ModuleDescriptor? module = FindModuleForAddress(SelectedInstructionAddress);
        if (module is null)
        {
            StatusText = "当前地址不在任何已加载模块中。";
            return false;
        }

        string search = query.Trim();
        ulong start = module.BaseAddress;
        ulong end = Math.Min(
            module.BaseAddress + module.Size,
            start + 16UL * 1024 * 1024);
        StatusText = "正在搜索指令...";
        InstructionSnapshot? match = await Task.Run(() =>
        {
            ulong address = start;
            int inspected = 0;
            while (address < end && inspected < 250_000)
            {
                IReadOnlyList<InstructionSnapshot> block =
                    _session.Disassemble(address, 256);
                if (block.Count == 0)
                {
                    break;
                }

                foreach (InstructionSnapshot candidate in block)
                {
                    inspected++;
                    if (candidate.Text.Contains(
                            search,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }

                InstructionSnapshot last = block[^1];
                ulong next = last.Address + (uint)Math.Max(1, last.Length);
                if (next <= address)
                {
                    break;
                }

                address = next;
            }

            return null;
        }).ConfigureAwait(true);

        if (match is null)
        {
            StatusText = $"未找到包含“{search}”的指令。";
            return false;
        }

        await ShowAddressAsync(match.Address).ConfigureAwait(true);
        StatusText = $"已定位指令：{match.Text}";
        return true;
    }

    public ModuleDescriptor? FindModuleForAddress(ulong address) =>
        ModuleCatalog.FindByAddress(Modules, address);

    public async Task<bool> AnalyzeSelectedAddressAsync(
        ModuleDescriptor module)
    {
        if (!HasSelectedInstruction)
        {
            return false;
        }

        return await CrossRefPanel.AnalyzeModuleAtAddressAsync(
                module,
                SelectedInstructionAddress)
            .ConfigureAwait(true);
    }

    public async Task<(ulong Start, ulong End)?> GetSelectedFunctionRangeAsync()
    {
        if (!HasSelectedInstruction)
        {
            return null;
        }

        ModuleDescriptor? module = FindModuleForAddress(SelectedInstructionAddress);
        if (module is null)
        {
            StatusText = "当前指令不属于任何已加载模块。";
            return null;
        }

        bool analyzed = await CrossRefPanel.AnalyzeModuleAtAddressAsync(
                module,
                SelectedInstructionAddress)
            .ConfigureAwait(true);
        if (!analyzed || CrossRefPanel.Database is not { } database)
        {
            return null;
        }

        uint rva = checked((uint)(SelectedInstructionAddress - module.BaseAddress));
        FunctionEntry? function = database.FindFunctionContaining(rva) ??
            database.FindFunctionAt(rva);
        if (function is null)
        {
            StatusText = "未能确定当前指令所在函数的边界。";
            return null;
        }

        return (
            module.BaseAddress + function.Value.StartRva,
            module.BaseAddress + function.Value.EndRva);
    }

    public Task RunInstructionSearchAsync(
        CommandSearchRequest request,
        CancellationToken cancellationToken = default) =>
        InstructionSearchPanel.RunAsync(request, cancellationToken);

    public Task RunInstructionReferenceSearchAsync(
        InstructionReferenceSearchRequest request,
        CancellationToken cancellationToken = default) =>
        InstructionSearchPanel.RunReferenceSearchAsync(request, cancellationToken);

    public bool AddHardwareBreakpointAtAddress(
        ulong address,
        BreakpointKind kind,
        string? condition = null)
    {
        if (!_session.HardwareBreakpoints.Add(address, kind, condition))
        {
            StatusText = "无法设置硬件断点（最多 4 个，或地址无效）。";
            return false;
        }

        RefreshBreakpoints();
        RefreshSelectedDisassemblyMetadata();
        StatusText = $"硬件断点已设置 @ 0x{address:X}。";
        return true;
    }

    public bool ChangeMemoryProtection(
        ulong address,
        uint newProtection)
    {
        MemoryRegionInfo? region = MemoryRegions.FirstOrDefault(
            item => address >= item.BaseAddress &&
                    address < item.BaseAddress + item.Size);
        if (region is null)
        {
            StatusText = "当前地址不在已枚举的内存区域中。";
            return false;
        }

        bool changed = _session.ChangeMemoryProtection(
            region.BaseAddress,
            region.Size,
            newProtection,
            out uint oldProtection);
        if (!changed)
        {
            StatusText = "修改内存属性失败。";
            return false;
        }

        StatusText =
            $"内存属性已修改: 0x{oldProtection:X} -> 0x{newProtection:X} @ 0x{region.BaseAddress:X}。";
        return true;
    }

    public bool SetRegisterValue(string name, string expression)
    {
        if (!TryParseAddress(expression, out ulong value))
        {
            StatusText = $"寄存器值无效: {expression}";
            return false;
        }

        if (!_session.Debugger.SetRegisterValue(name, value))
        {
            StatusText = $"无法修改寄存器 {name}。";
            return false;
        }

        RefreshRegisters(_session.Debugger.GetCurrentRegisters());
        StatusText = $"{name} 已设置为 0x{value:X}。";
        return true;
    }

    public void ToggleRegisterDecimalDisplay()
    {
        RegisterDecimalDisplay = !RegisterDecimalDisplay;
        if (_lastPauseRegisters is { } registers)
        {
            RefreshRegisters(registers, updatePrevious: false);
        }
    }

    public void ToggleRegisterChangeHighlight()
    {
        HighlightChangedRegisters = !HighlightChangedRegisters;
        if (_lastPauseRegisters is { } registers)
        {
            RefreshRegisters(registers, updatePrevious: false);
        }
    }

    public ulong? GetBookmark(int index) =>
        index is >= 0 and < 10 ? _disassemblyBookmarks[index] : null;

    public bool HasBookmark(int index) => GetBookmark(index) is not null;

    public void ToggleBookmark(int index)
    {
        if (index is < 0 or >= 10 || !HasSelectedInstruction)
        {
            return;
        }

        _disassemblyBookmarks[index] =
            _disassemblyBookmarks[index] == SelectedInstructionAddress
                ? null
                : SelectedInstructionAddress;
    }

    public async Task GoToBookmarkAsync(int index)
    {
        if (GetBookmark(index) is { } address)
        {
            await ShowAddressAsync(address).ConfigureAwait(true);
        }
    }

    private void RefreshSelectedDisassemblyMetadata()
    {
        if (_currentAddress is not { } address)
        {
            return;
        }

        _ = ShowAddressAsync(address, recordHistory: false);
    }

    private DisassemblyOptions CreateDisassemblyOptions(ulong address)
    {
        ModuleDescriptor? module = FindModuleForAddress(address);
        return new DisassemblyOptions
        {
            Syntax = DisassemblySyntax,
            AddressMode = DisassemblyAddressMode,
            RelativeBase = module?.BaseAddress ?? 0,
            ModuleName = module?.Name ?? string.Empty,
            Modules = Modules
                .Select(static item => new DisassemblyModuleRange(
                    item.Name,
                    item.BaseAddress,
                    item.Size))
                .ToArray(),
            BytesStyle = DisassemblyBytesStyle,
            ShowBytes = ShowDisassemblyBytes,
            UseSignedImmediateOperands = UseSignedImmediateOperands,
            ShowJumpArrows = ShowJumpArrows
        };
    }

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
        _session.HardwareBreakpoints.Clear();
        RefreshBreakpoints();
    }

    [RelayCommand]
    private void ToggleSelectedBreakpoint()
    {
        if (SelectedBreakpoint is null)
        {
            return;
        }

        BreakpointEntry breakpoint = SelectedBreakpoint;
        bool changed = breakpoint.Kind == BreakpointKind.Software
            ? breakpoint.IsEnabled
                ? _session.Breakpoints.Disable(breakpoint.Address)
                : _session.Breakpoints.Enable(breakpoint.Address)
            : breakpoint.IsEnabled
                ? _session.HardwareBreakpoints.Disable(breakpoint.Address)
                : _session.HardwareBreakpoints.Enable(breakpoint.Address);
        if (changed)
        {
            RefreshBreakpoints();
        }
    }

    public void ToggleBreakpointEnabled(BreakpointEntry breakpoint)
    {
        ArgumentNullException.ThrowIfNull(breakpoint);
        bool changed = breakpoint.Kind == BreakpointKind.Software
            ? breakpoint.IsEnabled
                ? _session.Breakpoints.Disable(breakpoint.Address)
                : _session.Breakpoints.Enable(breakpoint.Address)
            : breakpoint.IsEnabled
                ? _session.HardwareBreakpoints.Disable(breakpoint.Address)
                : _session.HardwareBreakpoints.Enable(breakpoint.Address);
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

        if (SelectedBreakpoint.Kind == BreakpointKind.Software)
        {
            _session.Breakpoints.Remove(SelectedBreakpoint.Address);
        }
        else
        {
            _session.HardwareBreakpoints.Remove(SelectedBreakpoint.Address);
        }

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

            ExceptionHandlerStatusText =
                $"VEH:  {VehEntries.Count}   .pdata:  {PdataEntries.Count}";
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
        bool recordHistory = true,
        ulong? hexAddress = null)
    {
        try
        {
            Disassembly.Clear();
            DisassemblyOptions options = CreateDisassemblyOptions(address);
            foreach (InstructionSnapshot instruction in
                     _session.Disassemble(address, 80, options))
            {
                instruction.Comment = GetInstructionComment(instruction.Address);
                instruction.CustomSymbolName =
                    GetInstructionCustomSymbol(instruction.Address);
                instruction.IsBreakpoint =
                    _session.Breakpoints.Contains(instruction.Address) ||
                    _session.HardwareBreakpoints.Find(instruction.Address) is not null;
                instruction.IsInstructionPointer =
                    _session.Debugger.IsDebugging &&
                    _session.Debugger.IsPaused &&
                    instruction.Address == _session.Debugger.CurrentInstructionPointer;
                if (!string.IsNullOrWhiteSpace(instruction.CustomSymbolName))
                {
                    instruction.DisplayAddressText = instruction.CustomSymbolName;
                }

                Disassembly.Add(instruction);
            }

            SelectedInstruction = Disassembly.FirstOrDefault();
            AddressInput = $"0x{address:X}";
            RefreshHex(hexAddress ?? address);
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
            OnPropertyChanged(nameof(HexRegionInfoText));
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
        NotifyStatusProperties();
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
        NotifyStatusProperties();
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

    private void NotifyStatusProperties()
    {
        OnPropertyChanged(nameof(StatusLabelText));
        OnPropertyChanged(nameof(StatusDescriptionText));
    }

    private void HandleDebuggerDiagnostic(string message)
    {
        WriteDiagnostic(message);

        _dispatcher.BeginInvoke(
            () => AppendLog($"调试诊断: {message}", LogCategory.DebugEvent));
    }

    private void HandleBreakpointChanged(BreakpointEntry breakpoint)
    {
        if (DispatchToUiIfNeeded(() => HandleBreakpointChanged(breakpoint)))
        {
            return;
        }

        RefreshBreakpoints();
    }

    private void HandleHardwareBreakpointHit(
        BreakpointEntry breakpoint,
        uint threadId)
    {
        if (DispatchToUiIfNeeded(() =>
                HandleHardwareBreakpointHit(breakpoint, threadId)))
        {
            return;
        }

        HardwareAccessRecords.Insert(
            0,
            new HardwareAccessRecord(
                DateTime.Now,
                breakpoint.Address,
                breakpoint.Kind,
                threadId));
        while (HardwareAccessRecords.Count > 2_000)
        {
            HardwareAccessRecords.RemoveAt(HardwareAccessRecords.Count - 1);
        }
    }

    partial void OnDisassemblySyntaxChanged(AssemblySyntax value) =>
        RefreshSelectedDisassemblyMetadata();

    partial void OnDisassemblyAddressModeChanged(AssemblyAddressMode value) =>
        RefreshSelectedDisassemblyMetadata();

    partial void OnDisassemblyBytesStyleChanged(DisassemblyBytesStyle value)
    {
        RefreshSelectedDisassemblyMetadata();
        if (_settings.Current.DisassemblyBytesStyle == (int)value)
        {
            return;
        }

        _settings.Current.DisassemblyBytesStyle = (int)value;
        _settings.Save();
    }

    partial void OnShowDisassemblyBytesChanged(bool value) =>
        RefreshSelectedDisassemblyMetadata();

    partial void OnUseSignedImmediateOperandsChanged(bool value) =>
        RefreshSelectedDisassemblyMetadata();

    partial void OnShowJumpArrowsChanged(bool value) =>
        RefreshSelectedDisassemblyMetadata();

    partial void OnHexDisplayModeChanged(HexDisplayMode value) =>
        RefreshHexFromCurrentAddress();

    partial void OnHexBytesPerRowChanged(int value)
    {
        OnPropertyChanged(nameof(HexBytesHeader));
        RefreshHexFromCurrentAddress();
    }

    partial void OnHexTextEncodingChanged(string value) =>
        RefreshHexFromCurrentAddress();

    partial void OnStatusTextChanged(string value) =>
        OnPropertyChanged(nameof(StatusDescriptionText));

    public string HexRegionInfoText
    {
        get
        {
            if (_currentAddress is not { } address)
            {
                return string.Empty;
            }

            MemoryRegionInfo? region = MemoryRegions.FirstOrDefault(
                item =>
                    address >= item.BaseAddress &&
                    address < item.BaseAddress + item.Size);
            if (region is null)
            {
                return string.Empty;
            }

            string module = string.IsNullOrWhiteSpace(region.ModuleName)
                ? string.Empty
                : $"  模块={region.ModuleName}";
            return $"保护: {region.ProtectDisplayText}"
                + $"  分配基址={region.AllocationBase:X}"
                + $"  基址={region.BaseAddress:X}"
                + $"  大小={region.Size:X}"
                + module;
        }
    }

    private void RefreshHexFromCurrentAddress()
    {
        if (_currentAddress is { } address && _session.Target.IsOpen)
        {
            RefreshHex(address);
        }
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
            ? $"{_session.Target.ProcessName} | PID: {_session.Target.ProcessId} | {(_session.Target.Is64Bit ? "x64" : "x86")}"
            : "未打开进程";
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(IsTargetOpen));
        LoadSymbolsCommand.NotifyCanExecuteChanged();
        CloseProcessHandleCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsTarget64Bit));
        OnPropertyChanged(nameof(TargetProcessId));
        OnPropertyChanged(nameof(TargetProcessName));
        OnPropertyChanged(nameof(TargetProcessPath));
        OnPropertyChanged(nameof(StatusLabelText));
        OnPropertyChanged(nameof(StatusDescriptionText));
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
            _session.HardwareBreakpoints.Clear();
            _nopUndoHistory.Clear();
            Array.Clear(_disassemblyBookmarks);
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
            await ShowAddressAsync(
                    address,
                    hexAddress: module?.BaseAddress ?? address)
                .ConfigureAwait(true);
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

        ThreadDescriptor[] threads = _session.EnumerateThreads()
            .OrderBy(static thread => thread.ThreadId)
            .ToArray();
        for (int index = 0; index < threads.Length; index++)
        {
            ThreadDescriptor thread = threads[index];
            thread.EntryText = FormatThreadEntry(thread.StartAddress);
            if (index == 0)
            {
                if (thread.Name.StartsWith("Thread ", StringComparison.Ordinal))
                {
                    thread.Name = "主线程";
                }

                thread.StatusText = "运行中";
            }

            Threads.Add(thread);
        }

        Handles.Clear();
        foreach (ProcessHandleEntry handle in _session.EnumerateHandles())
        {
            Handles.Add(handle);
        }

        RefreshBreakpoints();
        OnPropertyChanged(nameof(CommittedMemoryText));
    }

    private string FormatThreadEntry(ulong address)
    {
        ModuleDescriptor? module = Modules.FirstOrDefault(
            candidate => candidate.Contains(address));
        if (module is null)
        {
            return $"0x{address:X}";
        }

        string moduleName = string.IsNullOrWhiteSpace(module.Name)
            ? System.IO.Path.GetFileName(module.FilePath)
            : module.Name;
        return $"\"{moduleName}\"+{address - module.BaseAddress:X}";
    }

    private void AppendLog(
        string message,
        LogCategory category = LogCategory.General)
    {
        LogMessages.Add(new LogRow(DateTime.Now, message, category));
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

        IEnumerable<BreakpointEntry> entries = _session.Breakpoints.Entries
            .Concat(_session.HardwareBreakpoints.Entries)
            .OrderBy(static breakpoint => breakpoint.Address)
            .ThenBy(static breakpoint => breakpoint.Kind);
        foreach (BreakpointEntry breakpoint in entries)
        {
            ModuleDescriptor? module = ModuleCatalog.FindByAddress(
                Modules,
                breakpoint.Address);
            breakpoint.ModuleName = module?.Name ?? string.Empty;
            breakpoint.DisplayAddressText = module is null
                ? $"0x{breakpoint.Address:X}"
                : $"{module.Name}+{breakpoint.Address - module.BaseAddress:X}";
            breakpoint.SymbolName = CustomSymbols.FirstOrDefault(
                row => row.Address == breakpoint.Address)?.Name ?? string.Empty;
            Breakpoints.Add(breakpoint);
        }
    }

    private void RefreshRegisters(
        RegisterSnapshot registers,
        bool updatePrevious = true)
    {
        Registers.Clear();
        foreach ((string name, ulong value) in registers.GeneralPurposeRegisters)
        {
            bool changed =
                HighlightChangedRegisters &&
                _previousRegisterValues.TryGetValue(name, out ulong previous) &&
                previous != value;
            Registers.Add(new RegisterRow(
                name,
                value,
                RegisterDecimalDisplay,
                changed));
        }

        if (!updatePrevious)
        {
            return;
        }

        _previousRegisterValues.Clear();
        foreach ((string name, ulong value) in registers.GeneralPurposeRegisters)
        {
            _previousRegisterValues[name] = value;
        }
    }

    private void RefreshHex(ulong address)
    {
        HexLines.Clear();
        int bytesPerRow = Math.Clamp(HexBytesPerRow, 4, 32);
        ulong start = address & ~0xFUL;
        int totalBytes = bytesPerRow * 16;
        byte[] bytes = _session.ReadBytes(start, totalBytes);
        for (int offset = 0; offset < bytes.Length; offset += bytesPerRow)
        {
            int count = Math.Min(bytesPerRow, bytes.Length - offset);
            ReadOnlySpan<byte> row = bytes.AsSpan(offset, count);
            string hex = FormatHexRow(row);
            string ascii = DecodeHexText(row);
            HexLines.Add(new HexViewLine(start + (uint)offset, hex, ascii));
        }
    }

    private string FormatHexRow(ReadOnlySpan<byte> row)
    {
        byte[] bytes = row.ToArray();
        return HexDisplayMode switch
        {
            HexDisplayMode.Word => string.Join(
                ' ',
                Enumerable.Range(0, (bytes.Length + 1) / 2)
                    .Select(index => ReadLittleEndian(bytes, index * 2, 2)
                        .ToString("X4", CultureInfo.InvariantCulture))),
            HexDisplayMode.Dword => string.Join(
                ' ',
                Enumerable.Range(0, (bytes.Length + 3) / 4)
                    .Select(index => ReadLittleEndian(bytes, index * 4, 4)
                        .ToString("X8", CultureInfo.InvariantCulture))),
            HexDisplayMode.Qword => string.Join(
                ' ',
                Enumerable.Range(0, (bytes.Length + 7) / 8)
                    .Select(index => ReadLittleEndian(bytes, index * 8, 8)
                        .ToString("X16", CultureInfo.InvariantCulture))),
            HexDisplayMode.Float => string.Join(
                ' ',
                Enumerable.Range(0, (bytes.Length + 3) / 4)
                    .Select(index => BitConverter.ToSingle(
                            Pad(bytes, index * 4, 4))
                        .ToString("G6", CultureInfo.InvariantCulture))),
            HexDisplayMode.Double => string.Join(
                ' ',
                Enumerable.Range(0, (bytes.Length + 7) / 8)
                    .Select(index => BitConverter.ToDouble(
                            Pad(bytes, index * 8, 8))
                        .ToString("G10", CultureInfo.InvariantCulture))),
            _ => string.Join(
                ' ',
                bytes.Select(value => value.ToString(
                    "X2",
                    CultureInfo.InvariantCulture)))
        };
    }

    private string DecodeHexText(ReadOnlySpan<byte> row)
    {
        try
        {
            Encoding encoding = HexTextEncoding.ToUpperInvariant() switch
            {
                "UTF-8" => Encoding.UTF8,
                "UTF-16" => Encoding.Unicode,
                "GBK" => Encoding.GetEncoding(936),
                "BIG5" => Encoding.GetEncoding(950),
                _ => Encoding.ASCII
            };
            string value = encoding.GetString(row);
            return new string(value
                .Select(static character =>
                    char.IsControl(character) ? '.' : character)
                .ToArray());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static ulong ReadLittleEndian(
        byte[] bytes,
        int offset,
        int size)
    {
        byte[] value = Pad(bytes, offset, size);
        return size switch
        {
            2 => BitConverter.ToUInt16(value),
            4 => BitConverter.ToUInt32(value),
            8 => BitConverter.ToUInt64(value),
            _ => 0
        };
    }

    private static byte[] Pad(byte[] value, int offset, int size)
    {
        byte[] result = new byte[size];
        if (offset >= value.Length)
        {
            return result;
        }

        Array.Copy(
            value,
            offset,
            result,
            0,
            Math.Min(size, value.Length - offset));
        return result;
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

    private readonly record struct NopUndoRecord(
        ulong Address,
        byte[] OriginalBytes);
}

public sealed record HexViewLine(ulong Address, string Hex, string Ascii)
{
    public string AddressText => $"{Address:X16}";
}

public enum HexDisplayMode
{
    Hex,
    Word,
    Dword,
    Qword,
    Float,
    Double
}

public sealed record RegisterRow(
    string Name,
    ulong Value,
    bool DecimalDisplay,
    bool IsChanged)
{
    public string ValueText => DecimalDisplay
        ? Value.ToString(CultureInfo.InvariantCulture)
        : $"0x{Value:X}";
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

public enum LogCategory
{
    General,
    System,
    DebugEvent
}

public sealed record LogRow(
    DateTime Timestamp,
    string Message,
    LogCategory Category = LogCategory.General)
{
    public string TimestampText => Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
}

public sealed record HardwareAccessRecord(
    DateTime Timestamp,
    ulong Address,
    BreakpointKind Kind,
    uint ThreadId)
{
    public string AddressText => $"0x{Address:X}";

    public string KindText => Kind == BreakpointKind.HardwareWrite
        ? "写入"
        : "读取/访问";

    public string TimestampText =>
        Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
}

public sealed record ModuleImportRow(
    string AddressText,
    string ModuleName,
    string FunctionName);

public sealed record ModuleExportRow(
    string AddressText,
    string OrdinalText,
    string Name);
