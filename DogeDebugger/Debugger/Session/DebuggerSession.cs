using DogeDebugger.Core.Disassembly;
using DogeDebugger.Core.CrossReference;
using DogeDebugger.Core.ExceptionHandler;
using DogeDebugger.Core.Handles;
using DogeDebugger.Core.Memory;
using DogeDebugger.Core.Modules;
using DogeDebugger.Core.Process;
using DogeDebugger.Core.Rtti;
using DogeDebugger.Core.Search;
using DogeDebugger.Core.Signatures;
using DogeDebugger.Core.StringRef;
using DogeDebugger.Core.Threading;
using DogeDebugger.Debugger.Breakpoints;
using DogeDebugger.Debugger.UserMode;
using DogeDebugger.Plugins.UnrealEngine.Services;
using System.IO;

namespace DogeDebugger.Debugger.Session;

public sealed class DebuggerSession : IAsyncDisposable
{
    private readonly ProcessCatalog _processes = new();
    private readonly ModuleCatalog _modules = new();
    private readonly MemoryRegionCatalog _regions;
    private readonly ThreadCatalog _threads = new();
    private readonly ProcessHandleCatalog _handles = new();
    private readonly DisassemblerService _disassembler = new();
    private readonly MemorySearchService _search;
    private readonly MemoryValueScanner _valueScanner;
    private readonly PointerScanner _pointerScanner;
    private readonly StringScanner _stringScanner;
    private readonly XrefBuilder _xrefBuilder = new();
    private readonly PeModuleAnalyzer _peAnalyzer = new();
    private readonly ExceptionHandlerScanner _exceptionHandlerScanner = new();
    private readonly RttiScanner _rttiScanner = new();
    private readonly RttiWorkspaceService _rttiWorkspace = new();
    private readonly AobSignatureGenerator _signatureGenerator;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool _disposed;

    public DebuggerSession()
    {
        _regions = new MemoryRegionCatalog(_modules);
        _search = new MemorySearchService(_regions);
        _valueScanner = new MemoryValueScanner(_regions);
        _pointerScanner = new PointerScanner(_regions);
        _stringScanner = new StringScanner(_regions);
        _signatureGenerator = new AobSignatureGenerator(_search);
        Target = new TargetProcess();
        Debugger = new UserModeDebugger(Target);
        UnrealEngine = new UnrealEngineService(this);
        Debugger.Paused += HandlePaused;
        Debugger.DebugEventReceived += HandleDebugEventReceived;
        Debugger.StateChanged += HandleStateChanged;
    }

    public TargetProcess Target { get; }

    public UserModeDebugger Debugger { get; }

    public UnrealEngineService UnrealEngine { get; }

    public SoftwareBreakpointManager Breakpoints => Debugger.Breakpoints;

    public RttiWorkspaceService RttiWorkspace => _rttiWorkspace;

    public event EventHandler? TargetChanged;

    public event EventHandler<DebuggerPausedEventArgs>? Paused;

    public event EventHandler<DebuggerEventEventArgs>? DebugEventReceived;

    public event EventHandler<DebuggerStateChangedEventArgs>? StateChanged;

    public IReadOnlyList<ProcessDescriptor> EnumerateProcesses() => _processes.Enumerate();

    public IReadOnlyList<ModuleDescriptor> EnumerateModules()
    {
        EnsureOpen();
        return _modules.Enumerate(Target);
    }

    public IReadOnlyList<MemoryRegionInfo> EnumerateMemoryRegions()
    {
        EnsureOpen();
        return _regions.Enumerate(Target);
    }

    public IReadOnlyList<ThreadDescriptor> EnumerateThreads()
    {
        EnsureOpen();
        return _threads.Enumerate(Target);
    }

    public IReadOnlyList<ProcessHandleEntry> EnumerateHandles()
    {
        EnsureOpen();
        return _handles.Enumerate(Target);
    }

    public bool SetThreadSuspended(uint threadId, bool suspended)
    {
        EnsureOpen();
        return _threads.SetSuspended(threadId, suspended);
    }

    public PeModuleMetadata AnalyzeModule(ModuleDescriptor module)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(module);
        if (string.IsNullOrWhiteSpace(module.FilePath) || !File.Exists(module.FilePath))
        {
            throw new FileNotFoundException("The module file is not available on disk.", module.FilePath);
        }

        return _peAnalyzer.AnalyzeFile(module.FilePath);
    }

    public ExceptionHandlerScanResult ScanExceptionHandlers(
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _exceptionHandlerScanner.Scan(
            Target,
            _modules.Enumerate(Target),
            cancellationToken);
    }

    public RttiScanResult ScanRtti(CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _rttiScanner.Scan(
            Target,
            _modules.Enumerate(Target),
            cancellationToken);
    }

    public RttiInstance ReadRttiInstance(
        RttiClassDefinition definition,
        ulong? address = null)
    {
        EnsureOpen();
        return _rttiWorkspace.ReadInstance(Target, definition, address);
    }

    public IReadOnlyList<RttiFieldValue> ReadRttiValues(
        RttiClassDefinition definition,
        ulong? address = null)
    {
        EnsureOpen();
        return _rttiWorkspace.ReadValues(Target, definition, address);
    }

    public async Task<bool> OpenAsync(
        ProcessDescriptor process,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CloseCoreAsync().ConfigureAwait(false);
            bool opened = Target.Open(
                process.ProcessId,
                process.Name,
                process.FilePath ?? string.Empty);
            if (opened)
            {
                TargetChanged?.Invoke(this, EventArgs.Empty);
            }

            return opened;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> AttachAsync(
        ProcessDescriptor process,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CloseCoreAsync().ConfigureAwait(false);
            bool attached = await Debugger.AttachAsync(
                    process.ProcessId,
                    process.Name,
                    process.FilePath ?? string.Empty,
                    cancellationToken)
                .ConfigureAwait(false);
            if (attached)
            {
                TargetChanged?.Invoke(this, EventArgs.Empty);
            }

            return attached;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> LaunchAsync(
        string commandLine,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CloseCoreAsync().ConfigureAwait(false);
            bool launched = await Debugger.LaunchAsync(
                    commandLine,
                    workingDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
            if (launched)
            {
                TargetChanged?.Invoke(this, EventArgs.Empty);
            }

            return launched;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CloseCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public byte[] ReadBytes(ulong address, int size)
    {
        EnsureOpen();
        return Target.ReadBytes(address, size);
    }

    public bool WriteBytes(ulong address, ReadOnlySpan<byte> bytes)
    {
        EnsureOpen();
        return Target.TryWriteBytes(address, bytes);
    }

    public IReadOnlyList<InstructionSnapshot> Disassemble(
        ulong address,
        int instructionCount,
        DisassemblyOptions? options = null)
    {
        EnsureOpen();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(instructionCount);

        List<InstructionSnapshot> instructions = new(instructionCount);
        ulong current = address;
        Span<byte> instructionBytes = stackalloc byte[16];
        while (instructions.Count < instructionCount)
        {
            if (!Target.TryReadBytes(current, instructionBytes))
            {
                break;
            }

            InstructionSnapshot? instruction = _disassembler.DisassembleOne(
                instructionBytes,
                current,
                Target.Is64Bit,
                options);
            if (instruction is null || instruction.Length <= 0)
            {
                break;
            }

            instructions.Add(instruction);
            current += checked((uint)instruction.Length);
        }

        return instructions;
    }

    public SearchResult Search(
        SearchRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _search.Search(Target, request, cancellationToken);
    }

    public MemoryValueScanResult ScanValues(
        MemoryValueScanOptions options,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _valueScanner.InitialScan(Target, options, cancellationToken);
    }

    public MemoryValueScanResult RefineValues(
        IReadOnlyList<MemoryValueMatch> previous,
        MemoryValueScanOptions options,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _valueScanner.NextScan(Target, previous, options, cancellationToken);
    }

    public PointerScanResult ScanPointers(
        PointerScanOptions options,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _pointerScanner.Scan(
            Target,
            _modules.Enumerate(Target),
            options,
            cancellationToken);
    }

    public PointerScanResult RescanPointers(
        PointerScanResult previous,
        PointerScanRescanOptions options,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _pointerScanner.Rescan(
            Target,
            _modules.Enumerate(Target),
            previous,
            options,
            cancellationToken);
    }

    public IReadOnlyList<StringEntry> ScanStrings(
        StringScanOptions options,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _stringScanner.Scan(Target, options, cancellationToken);
    }

    public XrefDatabase BuildCrossReferences(CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _xrefBuilder.Build(
            Target,
            _modules.Enumerate(Target),
            _regions.Enumerate(Target),
            cancellationToken);
    }

    public XrefDatabase BuildModuleCrossReferences(
        ModuleDescriptor module,
        string? pdbPath = null,
        IProgress<(double Progress, string Message)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _xrefBuilder.BuildModule(
            Target,
            module,
            pdbPath,
            progress,
            cancellationToken);
    }

    public AobSignature? GenerateSignature(
        ulong address,
        ModuleDescriptor? module = null,
        AobSignatureOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        module ??= ModuleCatalog.FindByAddress(_modules.Enumerate(Target), address);
        return _signatureGenerator.Generate(
            Target,
            module,
            address,
            options,
            cancellationToken);
    }

    public ulong AllocateMemory(
        ulong preferredAddress,
        int size,
        uint protection)
    {
        EnsureOpen();
        return Target.AllocateMemory(preferredAddress, size, protection);
    }

    public bool FreeMemory(ulong address)
    {
        EnsureOpen();
        return Target.FreeMemory(address);
    }

    public int CreateRemoteThread(
        ulong startAddress,
        ulong parameter,
        uint creationFlags)
    {
        EnsureOpen();
        return Target.CreateRemoteThread(startAddress, parameter, creationFlags);
    }

    public uint CallRemoteFunction(
        ulong functionAddress,
        IReadOnlyList<ulong> arguments)
    {
        EnsureOpen();
        return Target.CallRemoteFunction(functionAddress, arguments);
    }

    public RemoteOperationResult CallRemoteFunction64(
        ulong functionAddress,
        IReadOnlyList<ulong> arguments,
        int timeoutMilliseconds = 5000,
        uint creationFlags = 0)
    {
        EnsureOpen();
        return Target.CallRemoteFunction64(
            functionAddress,
            arguments,
            timeoutMilliseconds,
            creationFlags);
    }

    public ulong InjectDll(string path)
    {
        EnsureOpen();
        return Target.InjectDll(path);
    }

    public RemoteOperationResult InjectDll(
        string path,
        string? exportName,
        int timeoutMilliseconds = 5000)
    {
        EnsureOpen();
        return Target.InjectDll(path, exportName, timeoutMilliseconds);
    }

    public RemoteOperationResult UnloadDll(
        string path,
        int timeoutMilliseconds = 5000)
    {
        EnsureOpen();
        return Target.UnloadDll(path, timeoutMilliseconds);
    }

    public bool AddBreakpoint(ulong address, bool temporary = false)
    {
        EnsureOpen();
        return Breakpoints.Add(address, temporary);
    }

    public bool RemoveBreakpoint(ulong address)
    {
        EnsureOpen();
        return Breakpoints.Remove(address);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await CloseCoreAsync().ConfigureAwait(false);
        Debugger.Paused -= HandlePaused;
        Debugger.DebugEventReceived -= HandleDebugEventReceived;
        Debugger.StateChanged -= HandleStateChanged;
        await Debugger.DisposeAsync().ConfigureAwait(false);
        Target.Dispose();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task CloseCoreAsync()
    {
        if (Debugger.IsDebugging)
        {
            await Debugger.DetachAsync(CancellationToken.None).ConfigureAwait(false);
        }

        Breakpoints.RestoreAll();
        Target.Close();
    }

    private void HandlePaused(object? sender, DebuggerPausedEventArgs args)
    {
        Paused?.Invoke(this, args);
    }

    private void HandleDebugEventReceived(object? sender, DebuggerEventEventArgs args)
    {
        DebugEventReceived?.Invoke(this, args);
    }

    private void HandleStateChanged(object? sender, DebuggerStateChangedEventArgs args)
    {
        StateChanged?.Invoke(this, args);
    }

    private void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Target.IsOpen)
        {
            throw new InvalidOperationException("No target process is open.");
        }
    }
}
