using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using DogeDebugger.Core.Disassembly;
using DogeDebugger.Core.Process;
using DogeDebugger.Debugger.Breakpoints;

namespace DogeDebugger.Debugger.UserMode;

public sealed class UserModeDebugger : IAsyncDisposable
{
    private const uint ExceptionBreakpoint = 0x80000003;
    private const uint ExceptionSingleStep = 0x80000004;
    private const uint ContextI386 = 0x00010000;
    private const uint ContextControl = 0x00000001;
    private const uint ContextInteger = 0x00000002;
    private const uint ContextDebugRegisters = 0x00000010;

    private readonly TargetProcess _target;
    private readonly DisassemblerService _disassembler = new();
    private readonly BreakpointConditionEvaluator _breakpointConditionEvaluator;
    private readonly Channel<DebuggerCommand> _commands;
    private readonly CancellationTokenSource _shutdown = new();
    private TaskCompletionSource<bool> _firstPause =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task? _eventLoop;
    private bool _attached;
    private bool _paused;
    private bool _expectInitialAttachBreakpoint;
    private bool _breakRequested;
    private bool _disposed;
    private uint _currentThreadId;
    private ulong _currentInstructionPointer;
    private PostBreakpointAction _postBreakpointAction;
    private ulong _pendingStepTarget;
    private ulong _pendingReturnAddress;
    private bool _suppressBreakpointWithSingleStep;
    private ulong? _temporarilySuppressedHardwareAddress;

    public UserModeDebugger(TargetProcess target)
    {
        _target = target;
        _breakpointConditionEvaluator = new BreakpointConditionEvaluator(target);
        _commands = Channel.CreateUnbounded<DebuggerCommand>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        Breakpoints = new SoftwareBreakpointManager(target);
        HardwareBreakpoints = new HardwareBreakpointManager();
        HardwareBreakpoints.Changed += ApplyHardwareBreakpoints;
    }

    public TargetProcess Target => _target;

    public SoftwareBreakpointManager Breakpoints { get; }

    public HardwareBreakpointManager HardwareBreakpoints { get; }

    public bool IsDebugging => _attached;

    public bool IsPaused => _paused;

    public uint CurrentThreadId => _currentThreadId;

    public ulong CurrentInstructionPointer => _currentInstructionPointer;

    public RegisterSnapshot GetCurrentRegisters() =>
        _currentThreadId == 0
            ? new RegisterSnapshot()
            : GetRegisters(_currentThreadId);

    public string? ValidateBreakpointCondition(string condition) =>
        BreakpointConditionEvaluator.ValidateSyntax(condition);

    internal void ReportDiagnostic(string message) =>
        Diagnostic?.Invoke(message);

    public bool SetCurrentInstructionPointer(ulong instructionPointer)
    {
        if (!_attached || !_paused || _currentThreadId == 0 || instructionPointer == 0)
        {
            return false;
        }

        if (!TrySetInstructionPointer(
                _currentThreadId,
                instructionPointer))
        {
            Diagnostic?.Invoke(
                $"Set instruction pointer failed: 0x{instructionPointer:X}");
            return false;
        }

        _currentInstructionPointer = instructionPointer;
        RaiseStateChanged();
        return true;
    }

    public bool SetRegisterValue(string name, ulong value)
    {
        if (!_attached || !_paused || _currentThreadId == 0 ||
            string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string register = name.Trim().TrimStart('$').ToUpperInvariant();
        if (register is "IP" or "RIP" or "EIP")
        {
            return SetCurrentInstructionPointer(value);
        }

        using DebugThreadHandle handle = new(
            NativeDebuggerMethods.OpenThread(
                NativeDebuggerMethods.ThreadGetContext |
                NativeDebuggerMethods.ThreadSetContext,
                inheritHandle: false,
                _currentThreadId));
        if (handle.IsInvalid)
        {
            return false;
        }

        if (_target.Is64Bit)
        {
            unsafe
            {
                NativeDebuggerMethods.Context64 context = new()
                {
                    ContextFlags = NativeDebuggerMethods.ContextAmd64 |
                                   ContextControl |
                                   ContextInteger
                };
                if (!NativeDebuggerMethods.GetThreadContext(
                        handle.DangerousGetHandle(),
                        &context))
                {
                    return false;
                }

                switch (register)
                {
                    case "RAX": context.Rax = value; break;
                    case "RBX": context.Rbx = value; break;
                    case "RCX": context.Rcx = value; break;
                    case "RDX": context.Rdx = value; break;
                    case "RSI": context.Rsi = value; break;
                    case "RDI": context.Rdi = value; break;
                    case "RBP": context.Rbp = value; break;
                    case "RSP": context.Rsp = value; break;
                    case "R8": context.R8 = value; break;
                    case "R9": context.R9 = value; break;
                    case "R10": context.R10 = value; break;
                    case "R11": context.R11 = value; break;
                    case "R12": context.R12 = value; break;
                    case "R13": context.R13 = value; break;
                    case "R14": context.R14 = value; break;
                    case "R15": context.R15 = value; break;
                    default: return false;
                }

                return NativeDebuggerMethods.SetThreadContext(
                    handle.DangerousGetHandle(),
                    &context);
            }
        }

        unsafe
        {
            NativeDebuggerMethods.Context32 context = new()
            {
                ContextFlags = ContextI386 |
                               ContextControl |
                               ContextInteger
            };
            if (!NativeDebuggerMethods.Wow64GetThreadContext(
                    handle.DangerousGetHandle(),
                    &context))
            {
                return false;
            }

            switch (register)
            {
                case "EAX": context.Eax = checked((uint)value); break;
                case "EBX": context.Ebx = checked((uint)value); break;
                case "ECX": context.Ecx = checked((uint)value); break;
                case "EDX": context.Edx = checked((uint)value); break;
                case "ESI": context.Esi = checked((uint)value); break;
                case "EDI": context.Edi = checked((uint)value); break;
                case "EBP": context.Ebp = checked((uint)value); break;
                case "ESP": context.Esp = checked((uint)value); break;
                default: return false;
            }

            return NativeDebuggerMethods.Wow64SetThreadContext(
                handle.DangerousGetHandle(),
                &context);
        }
    }

    public event EventHandler<DebuggerPausedEventArgs>? Paused;

    public event EventHandler<DebuggerEventEventArgs>? DebugEventReceived;

    public event Func<BreakpointFilterRequestEventArgs, CancellationToken, ValueTask<bool>>?
        BreakpointFilterRequested;

    public event Func<InternalBreakpointHitEventArgs, bool>? InternalBreakpointHit;

    public event EventHandler<DebuggerStateChangedEventArgs>? StateChanged;

    public event Action<string>? Diagnostic;

    public event Action<BreakpointEntry, uint>? HardwareBreakpointHit;

    public async Task<bool> AttachAsync(
        int processId,
        string processName,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_attached)
        {
            return false;
        }

        if (!_target.IsOpen && !_target.Open(processId, processName, filePath))
        {
            return false;
        }

        ResetFirstPauseSignal();
        _eventLoop = Task.Run(
            () => RunEventLoopOnDedicatedThread(
                () => AttachLoopAsync(processId, _shutdown.Token)),
            CancellationToken.None);

        try
        {
            Task firstPauseTask = _firstPause.Task.WaitAsync(cancellationToken);
            Task completedTask = await Task.WhenAny(firstPauseTask, _eventLoop)
                .ConfigureAwait(false);
            if (completedTask == _eventLoop)
            {
                await _eventLoop.ConfigureAwait(false);
                return _attached;
            }

            await firstPauseTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await DetachAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return _attached;
    }

    public async Task<bool> LaunchAsync(
        string commandLine,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        if (_attached)
        {
            return false;
        }

        ResetFirstPauseSignal();
        _eventLoop = Task.Run(
            () => RunEventLoopOnDedicatedThread(
                () => LaunchLoopAsync(commandLine, workingDirectory, _shutdown.Token)),
            CancellationToken.None);

        try
        {
            Task firstPauseTask = _firstPause.Task.WaitAsync(cancellationToken);
            Task completedTask = await Task.WhenAny(firstPauseTask, _eventLoop)
                .ConfigureAwait(false);
            if (completedTask == _eventLoop)
            {
                await _eventLoop.ConfigureAwait(false);
                return _attached;
            }

            await firstPauseTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await DetachAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return _attached;
    }

    private async Task AttachLoopAsync(int processId, CancellationToken cancellationToken)
    {
        try
        {
            if (!NativeDebuggerMethods.DebugActiveProcess(checked((uint)processId)))
            {
                Diagnostic?.Invoke(
                    $"DebugActiveProcess failed: {Marshal.GetLastWin32Error()}");
                return;
            }

            NativeDebuggerMethods.DebugSetProcessKillOnExit(false);
            _expectInitialAttachBreakpoint = true;
            _target.MarkDebuggerAttached();
            _attached = true;
            RaiseStateChanged();
            await EventLoopAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            Diagnostic?.Invoke($"Attach loop failed: {exception}");
            _attached = false;
        }
    }

    private async Task LaunchLoopAsync(
        string commandLine,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        NativeDebuggerMethods.StartupInfo startupInfo = new()
        {
            Size = Marshal.SizeOf<NativeDebuggerMethods.StartupInfo>()
        };

        if (!NativeDebuggerMethods.CreateProcessW(
                applicationName: null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                inheritHandles: false,
                NativeDebuggerMethods.DebugOnlyThisProcess |
                NativeDebuggerMethods.CreateUnicodeEnvironment,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfo,
                out NativeDebuggerMethods.ProcessInformation processInformation))
        {
            Diagnostic?.Invoke($"CreateProcessW failed: {Marshal.GetLastWin32Error()}");
            return;
        }

        try
        {
            int processId = checked((int)processInformation.ProcessId);
            string name = System.IO.Path.GetFileNameWithoutExtension(
                commandLine.Split(' ', 2)[0]);
            if (!_target.OpenFromHandle(
                    processId,
                    name,
                    string.Empty,
                    processInformation.Process))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not open the launched process.");
            }

            NativeDebuggerMethods.CloseHandle(processInformation.Thread);
            _expectInitialAttachBreakpoint = true;
            _target.MarkDebuggerAttached();
            _attached = true;
            Diagnostic?.Invoke(
                $"CreateProcess: pid={processId}, tid={processInformation.ThreadId}, debug={_attached}");
            RaiseStateChanged();
            await EventLoopAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            NativeDebuggerMethods.CloseHandle(processInformation.Process);
            NativeDebuggerMethods.CloseHandle(processInformation.Thread);
            Diagnostic?.Invoke($"Launch loop failed: {exception}");
            _attached = false;
        }
    }

    public Task<bool> ContinueAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync(DebuggerCommandKind.Continue, cancellationToken);

    public Task<bool> BreakAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync(DebuggerCommandKind.Break, cancellationToken);

    public Task<bool> StepIntoAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync(DebuggerCommandKind.StepInto, cancellationToken);

    public Task<bool> StepOverAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync(DebuggerCommandKind.StepOver, cancellationToken);

    public Task<bool> StepOutAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync(DebuggerCommandKind.StepOut, cancellationToken);

    public async Task DetachAsync(CancellationToken cancellationToken = default)
    {
        if (!_attached)
        {
            return;
        }

        bool detached = await SendCommandAsync(DebuggerCommandKind.Detach, cancellationToken)
            .ConfigureAwait(false);
        if (!detached && _attached)
        {
            await StopEventLoopAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await DetachAsync(CancellationToken.None).ConfigureAwait(false);
        Breakpoints.Dispose();
        HardwareBreakpoints.Changed -= ApplyHardwareBreakpoints;
        HardwareBreakpoints.Dispose();
        _shutdown.Cancel();
        _shutdown.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private async Task<bool> SendCommandAsync(
        DebuggerCommandKind kind,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!_attached)
        {
            return false;
        }

        TaskCompletionSource<bool> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        DebuggerCommand command = new(kind, completion);
        await _commands.Writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ResetFirstPauseSignal() =>
        _firstPause = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

    private void SignalFirstPause() =>
        _firstPause.TrySetResult(true);

    /// <summary>
    /// Runs the debugger event loop on one dedicated thread. Windows requires
    /// WaitForDebugEvent and ContinueDebugEvent for a debugged process to be
    /// called from the thread that owns the debug event; letting async
    /// continuations hop between thread-pool threads makes ContinueDebugEvent
    /// fail with ERROR_INVALID_HANDLE. A single-threaded synchronization
    /// context keeps every continuation on the owning thread.
    /// </summary>
    private static void RunEventLoopOnDedicatedThread(Func<Task> loopFactory)
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        DebuggerLoopSynchronizationContext context = new();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            Task loop = loopFactory();
            _ = loop.ContinueWith(
                _ => context.Complete(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            context.Run();
            loop.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private async Task EventLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _attached)
            {
                while (_commands.Reader.TryRead(out DebuggerCommand runningCommand))
                {
                    await HandleRunningCommandAsync(runningCommand, cancellationToken);
                }

                if (!NativeDebuggerMethods.WaitForDebugEvent(
                        out NativeDebuggerMethods.DebugEvent debugEvent,
                        milliseconds: 50))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error is not 0 and not 121 and not 258)
                    {
                        Diagnostic?.Invoke($"WaitForDebugEvent failed: {error}");
                        _attached = false;
                        break;
                    }

                    continue;
                }

                Diagnostic?.Invoke(
                    $"DebugEvent: code={(uint)debugEvent.Code}, pid={debugEvent.ProcessId}, tid={debugEvent.ThreadId}");
                DebuggerEventRecord record = CreateEventRecord(debugEvent);
                DebugEventReceived?.Invoke(this, new DebuggerEventEventArgs { Event = record });

                bool shouldPause = HandleDebugEvent(
                    debugEvent,
                    out DebuggerPauseReason pauseReason,
                    out uint continueStatus);

                if (shouldPause &&
                    await ShouldSuppressBreakpointAsync(
                        record,
                        debugEvent,
                        cancellationToken))
                {
                    if (_suppressBreakpointWithSingleStep)
                    {
                        _suppressBreakpointWithSingleStep = false;
                        SetTrapFlag(debugEvent.ThreadId, enabled: true);
                        _postBreakpointAction = PostBreakpointAction.Continue;
                    }
                    else
                    {
                        Breakpoints.ArmAll();
                    }

                    if (!NativeDebuggerMethods.ContinueDebugEvent(
                            debugEvent.ProcessId,
                            debugEvent.ThreadId,
                            continueStatus))
                    {
                        return;
                    }

                    continue;
                }

                if (shouldPause)
                {
                    _paused = true;
                    _currentThreadId = debugEvent.ThreadId;
                    _currentInstructionPointer = record.Address;
                    RaiseStateChanged();
                    SignalFirstPause();

                    RegisterSnapshot registers = GetRegisters(debugEvent.ThreadId);
                    Paused?.Invoke(
                        this,
                        new DebuggerPausedEventArgs
                        {
                            Reason = pauseReason,
                            Event = record,
                            Registers = registers
                        });

                    bool continued = await WaitForPausedCommandAsync(
                            debugEvent,
                            continueStatus,
                            cancellationToken);
                    if (!continued)
                    {
                        return;
                    }

                    _paused = false;
                    RaiseStateChanged();
                }
                else
                {
                    if (!NativeDebuggerMethods.ContinueDebugEvent(
                            debugEvent.ProcessId,
                            debugEvent.ThreadId,
                            continueStatus))
                    {
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _attached = false;
            _paused = false;
            Diagnostic?.Invoke("Debug event loop exited.");
            _target.MarkDebuggerDetached();
            RaiseStateChanged();
        }
    }

    private async Task<bool> WaitForPausedCommandAsync(
        NativeDebuggerMethods.DebugEvent debugEvent,
        uint initialContinueStatus,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            DebuggerCommand command = await _commands.Reader.ReadAsync(cancellationToken);

            // Breaking an already-paused debuggee is a successful no-op; the
            // pending debug event must stay pending until a resume command.
            if (command.Kind == DebuggerCommandKind.Break)
            {
                command.Completion.TrySetResult(true);
                continue;
            }

            ResumePlan plan = new(initialContinueStatus, ResumeMode.Continue, 0);
            bool handled = command.Kind switch
            {
                DebuggerCommandKind.Continue => TryBuildContinuePlan(
                    debugEvent,
                    out plan),
                DebuggerCommandKind.StepInto => TryBuildStepIntoPlan(
                    debugEvent,
                    out plan),
                DebuggerCommandKind.StepOver => TryBuildStepOverPlan(
                    debugEvent,
                    out plan),
                DebuggerCommandKind.StepOut => TryBuildStepOutPlan(
                    debugEvent,
                    out plan),
                DebuggerCommandKind.Detach => TryDetach(
                    debugEvent,
                    command,
                    out _),
                _ => false
            };

            if (!handled)
            {
                command.Completion.TrySetResult(false);
            }

            if (command.Kind == DebuggerCommandKind.Detach)
            {
                command.Completion.TrySetResult(handled);
                return false;
            }

            if (!handled)
            {
                continue;
            }

            ApplyResumePlan(debugEvent, plan);
            bool continued = NativeDebuggerMethods.ContinueDebugEvent(
                debugEvent.ProcessId,
                debugEvent.ThreadId,
                plan.ContinueStatus);
            for (int attempt = 1; !continued && attempt <= 2; attempt++)
            {
                int error = Marshal.GetLastWin32Error();
                Diagnostic?.Invoke(
                    $"ContinueDebugEvent failed: error={error}; retry {attempt}/2");
                Thread.Sleep(10 * attempt);
                continued = NativeDebuggerMethods.ContinueDebugEvent(
                    debugEvent.ProcessId,
                    debugEvent.ThreadId,
                    plan.ContinueStatus);
            }

            if (!continued)
            {
                Diagnostic?.Invoke(
                    $"ContinueDebugEvent retry failed: error={Marshal.GetLastWin32Error()}");
                command.Completion.TrySetResult(false);
                return false;
            }

            command.Completion.TrySetResult(true);
            return true;
        }

        return false;
    }

    private static bool TryBuildContinuePlan(
        NativeDebuggerMethods.DebugEvent debugEvent,
        out ResumePlan plan)
    {
        plan = new ResumePlan(
            NativeDebuggerMethods.DbContinue,
            ResumeMode.Continue,
            debugEvent.ThreadId);
        return true;
    }

    private bool TryBuildStepIntoPlan(
        NativeDebuggerMethods.DebugEvent debugEvent,
        out ResumePlan plan)
    {
        plan = new ResumePlan(
            NativeDebuggerMethods.DbContinue,
            ResumeMode.StepInto,
            debugEvent.ThreadId);
        return true;
    }

    private bool TryBuildStepOverPlan(
        NativeDebuggerMethods.DebugEvent debugEvent,
        out ResumePlan plan)
    {
        RegisterSnapshot registers = GetRegisters(debugEvent.ThreadId);
        ulong address = registers.InstructionPointer;
        Span<byte> bytes = stackalloc byte[16];
        if (!_target.TryReadBytes(address, bytes))
        {
            plan = default;
            return false;
        }

        InstructionSnapshot? instruction = _disassembler.DisassembleOne(
            bytes,
            address,
            _target.Is64Bit);
        if (instruction is null)
        {
            plan = default;
            return false;
        }

        plan = new ResumePlan(
            NativeDebuggerMethods.DbContinue,
            instruction.IsCall ? ResumeMode.StepOverCall : ResumeMode.StepInto,
            debugEvent.ThreadId,
            address + checked((uint)instruction.Length));
        return true;
    }

    private bool TryBuildStepOutPlan(
        NativeDebuggerMethods.DebugEvent debugEvent,
        out ResumePlan plan)
    {
        RegisterSnapshot registers = GetRegisters(debugEvent.ThreadId);
        Span<byte> returnValue = stackalloc byte[sizeof(ulong)];
        int size = _target.Is64Bit ? sizeof(ulong) : sizeof(uint);
        if (!_target.TryReadBytes(registers.StackPointer, returnValue[..size]))
        {
            plan = default;
            return false;
        }

        ulong returnAddress = _target.Is64Bit
            ? BitConverter.ToUInt64(returnValue)
            : BitConverter.ToUInt32(returnValue);
        if (returnAddress == 0)
        {
            plan = default;
            return false;
        }

        plan = new ResumePlan(
            NativeDebuggerMethods.DbContinue,
            ResumeMode.StepOutReturn,
            debugEvent.ThreadId,
            returnAddress);
        return true;
    }

    private bool TryDetach(
        NativeDebuggerMethods.DebugEvent debugEvent,
        DebuggerCommand command,
        out ResumePlan plan)
    {
        Breakpoints.RestoreAll();
        NativeDebuggerMethods.ContinueDebugEvent(
            debugEvent.ProcessId,
            debugEvent.ThreadId,
            NativeDebuggerMethods.DbContinue);
        NativeDebuggerMethods.DebugActiveProcessStop(debugEvent.ProcessId);
        _attached = false;
        _paused = false;
        _target.MarkDebuggerDetached();
        command.Completion.TrySetResult(true);
        plan = default;
        return true;
    }

    private void ApplyResumePlan(
        NativeDebuggerMethods.DebugEvent debugEvent,
        ResumePlan plan)
    {
        SoftwareBreakpointManager manager = Breakpoints;
        bool atSoftwareBreakpoint =
            manager.Find(_currentInstructionPointer) is { IsEnabled: true, IsArmed: false };

        if (plan.Mode == ResumeMode.Continue)
        {
            if (atSoftwareBreakpoint)
            {
                SetTrapFlag(debugEvent.ThreadId, enabled: true);
                _postBreakpointAction = PostBreakpointAction.Continue;
                return;
            }

            manager.ArmAll();
            return;
        }

        _pendingStepTarget = plan.TargetAddress;
        _pendingReturnAddress = plan.TargetAddress;

        if (atSoftwareBreakpoint)
        {
            SetTrapFlag(debugEvent.ThreadId, enabled: true);
            _postBreakpointAction = plan.Mode switch
            {
                ResumeMode.StepInto => PostBreakpointAction.Pause,
                ResumeMode.StepOverCall => PostBreakpointAction.StepOverCall,
                ResumeMode.StepOutReturn => PostBreakpointAction.StepOut,
                _ => PostBreakpointAction.Continue
            };
            return;
        }

        switch (plan.Mode)
        {
            case ResumeMode.StepInto:
                SetTrapFlag(debugEvent.ThreadId, enabled: true);
                break;

            case ResumeMode.StepOverCall:
            case ResumeMode.StepOutReturn:
                if (!Breakpoints.Add(plan.TargetAddress, temporary: true))
                {
                    SetTrapFlag(debugEvent.ThreadId, enabled: true);
                }
                break;
        }
    }

    private bool HandleDebugEvent(
        NativeDebuggerMethods.DebugEvent debugEvent,
        out DebuggerPauseReason pauseReason,
        out uint continueStatus)
    {
        pauseReason = default;
        continueStatus = NativeDebuggerMethods.DbContinue;

        switch (debugEvent.Code)
        {
            case NativeDebuggerMethods.DebugEventCode.CreateProcess:
                CloseDebugEventHandle(debugEvent.Union.CreateProcess.File);
                CloseDebugEventHandle(debugEvent.Union.CreateProcess.Thread);
                _target.MarkDebuggerAttached();
                return false;

            case NativeDebuggerMethods.DebugEventCode.LoadDll:
                CloseDebugEventHandle(debugEvent.Union.LoadDll.File);
                return false;

            case NativeDebuggerMethods.DebugEventCode.CreateThread:
                ApplyHardwareBreakpoints(debugEvent.ThreadId);
                return false;

            case NativeDebuggerMethods.DebugEventCode.Exception:
                return HandleException(debugEvent, out pauseReason, out continueStatus);

            case NativeDebuggerMethods.DebugEventCode.ExitProcess:
            case NativeDebuggerMethods.DebugEventCode.ExitThread:
                return false;

            default:
                return false;
        }
    }

    private bool HandleException(
        NativeDebuggerMethods.DebugEvent debugEvent,
        out DebuggerPauseReason pauseReason,
        out uint continueStatus)
    {
        continueStatus = NativeDebuggerMethods.DbContinue;
        NativeDebuggerMethods.ExceptionRecord exception =
            debugEvent.Union.Exception.ExceptionRecord;
        ulong exceptionAddress = exception.ExceptionAddress;

        if (exception.ExceptionCode == ExceptionBreakpoint)
        {
            if (Breakpoints.HandleHit(exceptionAddress, out BreakpointEntry? breakpoint) &&
                breakpoint is not null)
            {
                if (!TrySetInstructionPointer(
                        debugEvent.ThreadId,
                        breakpoint.Address))
                {
                    Diagnostic?.Invoke(
                        $"Restore breakpoint instruction pointer failed: " +
                        $"0x{breakpoint.Address:X}");
                }

                _currentInstructionPointer = breakpoint.Address;

                if (breakpoint.IsTemporary)
                {
                    Breakpoints.Remove(breakpoint.Address);
                    Breakpoints.ArmAll();
                    pauseReason = DebuggerPauseReason.SingleStep;
                    return true;
                }

                pauseReason = DebuggerPauseReason.Breakpoint;
                return true;
            }

            pauseReason = DebuggerPauseReason.SystemBreakpoint;
            if (_expectInitialAttachBreakpoint)
            {
                // Attach-time process breakpoints are transport noise, not a
                // user-visible pause. Release the attach waiter and continue
                // so the UI can select the primary module entry point.
                _expectInitialAttachBreakpoint = false;
                SignalFirstPause();
                return false;
            }

            if (_breakRequested)
            {
                _breakRequested = false;
                return true;
            }

            // Thread-start breakpoints are generated for every new thread while
            // a debugger is attached; only an explicit Break request or the
            // initial attach breakpoint should stop the debuggee.
            return false;
        }

        if (exception.ExceptionCode == ExceptionSingleStep)
        {
            return HandleSingleStep(debugEvent, out pauseReason);
        }

        continueStatus = NativeDebuggerMethods.DbExceptionNotHandled;
        pauseReason = DebuggerPauseReason.Exception;
        return true;
    }

    private bool HandleSingleStep(
        NativeDebuggerMethods.DebugEvent debugEvent,
        out DebuggerPauseReason pauseReason)
    {
        pauseReason = DebuggerPauseReason.SingleStep;
        SetTrapFlag(debugEvent.ThreadId, enabled: false);

        if (TryHandleHardwareBreakpoint(
                debugEvent.ThreadId,
                out bool suppressHardwareBreakpoint))
        {
            if (suppressHardwareBreakpoint)
            {
                _postBreakpointAction = PostBreakpointAction.Continue;
                return false;
            }

            pauseReason = DebuggerPauseReason.Breakpoint;
            return true;
        }

        if (_postBreakpointAction == PostBreakpointAction.Continue)
        {
            _postBreakpointAction = PostBreakpointAction.None;
            if (_temporarilySuppressedHardwareAddress is not null)
            {
                _temporarilySuppressedHardwareAddress = null;
                ApplyHardwareBreakpoints();
            }

            Breakpoints.ArmAll();
            return false;
        }

        if (_postBreakpointAction is PostBreakpointAction.StepOverCall or PostBreakpointAction.StepOut)
        {
            ulong target = _postBreakpointAction == PostBreakpointAction.StepOverCall
                ? _pendingStepTarget
                : _pendingReturnAddress;
            _postBreakpointAction = PostBreakpointAction.None;
            Breakpoints.ArmAll();
            if (target != 0)
            {
                Breakpoints.Add(target, temporary: true);
                return false;
            }
        }

        _postBreakpointAction = PostBreakpointAction.None;
        return true;
    }

    private async ValueTask<bool> ShouldSuppressBreakpointAsync(
        DebuggerEventRecord record,
        NativeDebuggerMethods.DebugEvent debugEvent,
        CancellationToken cancellationToken)
    {
        if (record.Kind != DebuggerEventKind.Exception ||
            record.ExceptionCode != ExceptionBreakpoint)
        {
            return false;
        }

        BreakpointEntry? breakpoint = null;
        ulong breakpointAddress = 0;
        if (Breakpoints.TryResolveHit(record.Address, out breakpoint) &&
            breakpoint is not null)
        {
            breakpointAddress = breakpoint.Address;
        }
        if (breakpoint is { IsInternal: true } &&
            InternalBreakpointHit is not null)
        {
            InternalBreakpointHitEventArgs internalHit = new()
            {
                Address = breakpointAddress,
                OwnerId = breakpoint.InternalOwnerId,
                ThreadId = debugEvent.ThreadId,
                Registers = GetRegisters(debugEvent.ThreadId)
            };
            foreach (Delegate handler in InternalBreakpointHit.GetInvocationList())
            {
                if (handler is not Func<InternalBreakpointHitEventArgs, bool> internalHandler)
                {
                    continue;
                }

                if (internalHandler(internalHit))
                {
                    return false;
                }
            }
        }

        if (breakpoint is not null &&
            !string.IsNullOrWhiteSpace(breakpoint.Condition))
        {
            BreakpointConditionResult condition =
                _breakpointConditionEvaluator.Evaluate(
                    breakpoint,
                    GetRegisters(debugEvent.ThreadId));
            Breakpoints.SetConditionError(
                breakpoint.Address,
                condition.HasError);
            if (condition.HasError)
            {
                Diagnostic?.Invoke(
                    $"Breakpoint condition error @ 0x{breakpoint.Address:X}: " +
                    condition.ErrorMessage);
                return false;
            }

            if (!condition.ShouldBreak)
            {
                Breakpoints.RecordConditionMiss(breakpoint.Address);
                _suppressBreakpointWithSingleStep = true;
                return true;
            }
        }

        if (BreakpointFilterRequested is null)
        {
            return false;
        }

        BreakpointFilterRequestEventArgs request = new()
        {
            Address = breakpointAddress,
            ProcessId = debugEvent.ProcessId,
            ThreadId = debugEvent.ThreadId,
            InstructionPointer = record.Address
        };

        foreach (Delegate handler in BreakpointFilterRequested.GetInvocationList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (handler is not Func<BreakpointFilterRequestEventArgs, CancellationToken, ValueTask<bool>>
                filter)
            {
                continue;
            }

            try
            {
                if (await filter(request, cancellationToken))
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Diagnostic?.Invoke($"Breakpoint filter failed: {exception}");
            }
        }

        return false;
    }

    private async Task HandleRunningCommandAsync(
        DebuggerCommand command,
        CancellationToken cancellationToken)
    {
        switch (command.Kind)
        {
            case DebuggerCommandKind.Break:
                bool breakRequested = _target.IsOpen &&
                                      NativeDebuggerMethods.DebugBreakProcess(_target.Handle);
                _breakRequested = breakRequested;
                if (!breakRequested)
                {
                    Diagnostic?.Invoke(
                        $"DebugBreakProcess failed: error={Marshal.GetLastWin32Error()}");
                }

                command.Completion.TrySetResult(breakRequested);
                break;

            case DebuggerCommandKind.Detach:
                Breakpoints.RestoreAll();
                if (!NativeDebuggerMethods.DebugActiveProcessStop(checked((uint)_target.ProcessId)))
                {
                    command.Completion.TrySetResult(false);
                    return;
                }

                _attached = false;
                _target.MarkDebuggerDetached();
                command.Completion.TrySetResult(true);
                await Task.Yield();
                break;

            default:
                command.Completion.TrySetResult(false);
                break;
        }
    }

    private RegisterSnapshot GetRegisters(uint threadId)
    {
        using DebugThreadHandle handle = new(
            NativeDebuggerMethods.OpenThread(
                NativeDebuggerMethods.ThreadGetContext |
                NativeDebuggerMethods.ThreadQueryInformation,
                inheritHandle: false,
                threadId));

        if (handle.IsInvalid)
        {
            return new RegisterSnapshot();
        }

        if (_target.Is64Bit)
        {
            unsafe
            {
                NativeDebuggerMethods.Context64 context = new()
                {
                    ContextFlags = NativeDebuggerMethods.ContextAmd64 |
                                   ContextControl
                };
                if (!NativeDebuggerMethods.GetThreadContext(
                        handle.DangerousGetHandle(),
                        &context))
                {
                    return new RegisterSnapshot();
                }

                return new RegisterSnapshot
                {
                    InstructionPointer = context.Rip,
                    StackPointer = context.Rsp,
                    FramePointer = context.Rbp,
                    Flags = context.EFlags,
                    GeneralPurposeRegisters = new Dictionary<string, ulong>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["RAX"] = context.Rax,
                        ["RCX"] = context.Rcx,
                        ["RDX"] = context.Rdx,
                        ["RBX"] = context.Rbx,
                        ["RSP"] = context.Rsp,
                        ["RBP"] = context.Rbp,
                        ["RSI"] = context.Rsi,
                        ["RDI"] = context.Rdi,
                        ["R8"] = context.R8,
                        ["R9"] = context.R9,
                        ["R10"] = context.R10,
                        ["R11"] = context.R11,
                        ["R12"] = context.R12,
                        ["R13"] = context.R13,
                        ["R14"] = context.R14,
                        ["R15"] = context.R15,
                        ["RIP"] = context.Rip
                    }
                };
            }
        }

        unsafe
        {
            NativeDebuggerMethods.Context32 context = new()
            {
                ContextFlags = ContextI386 |
                               ContextControl |
                               ContextInteger |
                               ContextDebugRegisters
            };
            if (!NativeDebuggerMethods.Wow64GetThreadContext(
                    handle.DangerousGetHandle(),
                    &context))
            {
                return new RegisterSnapshot();
            }

            return new RegisterSnapshot
            {
                InstructionPointer = context.Eip,
                StackPointer = context.Esp,
                FramePointer = context.Ebp,
                Flags = context.EFlags,
                GeneralPurposeRegisters = new Dictionary<string, ulong>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["EAX"] = context.Eax,
                    ["ECX"] = context.Ecx,
                    ["EDX"] = context.Edx,
                    ["EBX"] = context.Ebx,
                    ["ESP"] = context.Esp,
                    ["EBP"] = context.Ebp,
                    ["ESI"] = context.Esi,
                    ["EDI"] = context.Edi,
                    ["EIP"] = context.Eip
                }
            };
        }
    }

    private bool TrySetInstructionPointer(
        uint threadId,
        ulong instructionPointer)
    {
        using DebugThreadHandle handle = new(
            NativeDebuggerMethods.OpenThread(
                NativeDebuggerMethods.ThreadGetContext |
                NativeDebuggerMethods.ThreadSetContext,
                inheritHandle: false,
                threadId));

        if (handle.IsInvalid)
        {
            return false;
        }

        if (_target.Is64Bit)
        {
            unsafe
            {
                NativeDebuggerMethods.Context64 context = new()
                {
                    ContextFlags = NativeDebuggerMethods.ContextAmd64 |
                                   ContextControl |
                                   ContextInteger |
                                   ContextDebugRegisters
                };
                if (NativeDebuggerMethods.GetThreadContext(
                        handle.DangerousGetHandle(),
                        &context))
                {
                    context.Rip = instructionPointer;
                    return NativeDebuggerMethods.SetThreadContext(
                        handle.DangerousGetHandle(),
                        &context);
                }
            }

            return false;
        }

        unsafe
        {
            NativeDebuggerMethods.Context32 context = new()
            {
                ContextFlags = ContextI386 |
                               ContextControl |
                               ContextInteger |
                               ContextDebugRegisters
            };
            if (NativeDebuggerMethods.Wow64GetThreadContext(
                    handle.DangerousGetHandle(),
                    &context))
            {
                context.Eip = checked((uint)instructionPointer);
                return NativeDebuggerMethods.Wow64SetThreadContext(
                    handle.DangerousGetHandle(),
                    &context);
            }
        }

        return false;
    }

    private void SetTrapFlag(uint threadId, bool enabled)
    {
        using DebugThreadHandle handle = new(
            NativeDebuggerMethods.OpenThread(
                NativeDebuggerMethods.ThreadGetContext |
                NativeDebuggerMethods.ThreadSetContext,
                inheritHandle: false,
                threadId));

        if (handle.IsInvalid)
        {
            return;
        }

        if (_target.Is64Bit)
        {
            unsafe
            {
                NativeDebuggerMethods.Context64 context = new()
                {
                    ContextFlags = NativeDebuggerMethods.ContextAmd64 |
                                   ContextControl |
                                   ContextInteger |
                                   ContextDebugRegisters
                };
                if (NativeDebuggerMethods.GetThreadContext(
                        handle.DangerousGetHandle(),
                        &context))
                {
                    context.EFlags = enabled
                        ? context.EFlags | 0x100
                        : context.EFlags & ~0x100u;
                    if (!NativeDebuggerMethods.SetThreadContext(
                            handle.DangerousGetHandle(),
                            &context))
                    {
                        Diagnostic?.Invoke(
                            $"Set trap flag failed for thread {threadId}.");
                    }
                }
            }

            return;
        }

        unsafe
        {
            NativeDebuggerMethods.Context32 context = new()
            {
                ContextFlags = ContextI386 |
                               ContextControl
            };
            if (NativeDebuggerMethods.Wow64GetThreadContext(
                    handle.DangerousGetHandle(),
                    &context))
            {
                context.EFlags = enabled
                    ? context.EFlags | 0x100u
                    : context.EFlags & ~0x100u;
                if (!NativeDebuggerMethods.Wow64SetThreadContext(
                        handle.DangerousGetHandle(),
                        &context))
                {
                    Diagnostic?.Invoke(
                        $"Set trap flag failed for thread {threadId}.");
                }
            }
        }
    }

    private void ApplyHardwareBreakpoints()
    {
        if (!_attached || !_target.IsOpen)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process process =
                System.Diagnostics.Process.GetProcessById(_target.ProcessId);
            foreach (System.Diagnostics.ProcessThread thread in process.Threads)
            {
                ApplyHardwareBreakpoints(checked((uint)thread.Id));
            }
        }
        catch (Exception exception)
        {
            Diagnostic?.Invoke($"Apply hardware breakpoints failed: {exception.Message}");
        }
    }

    private void ApplyHardwareBreakpoints(uint threadId)
    {
        BreakpointEntry[] active = HardwareBreakpoints.Entries
            .Where(static entry => entry.IsEnabled)
            .Where(entry => entry.Address != _temporarilySuppressedHardwareAddress)
            .Take(4)
            .ToArray();

        using DebugThreadHandle handle = new(
            NativeDebuggerMethods.OpenThread(
                NativeDebuggerMethods.ThreadGetContext |
                NativeDebuggerMethods.ThreadSetContext,
                inheritHandle: false,
                threadId));
        if (handle.IsInvalid)
        {
            return;
        }

        if (_target.Is64Bit)
        {
            unsafe
            {
                NativeDebuggerMethods.Context64 context = new()
                {
                    ContextFlags = NativeDebuggerMethods.ContextAmd64 |
                                   ContextControl |
                                   ContextInteger |
                                   ContextDebugRegisters
                };
                if (!NativeDebuggerMethods.GetThreadContext(
                        handle.DangerousGetHandle(),
                        &context))
                {
                    return;
                }

                ApplyHardwareBreakpoints64(ref context, active);
                NativeDebuggerMethods.SetThreadContext(
                    handle.DangerousGetHandle(),
                    &context);
            }

            return;
        }

        unsafe
        {
            NativeDebuggerMethods.Context32 context = new()
            {
                ContextFlags = ContextI386 |
                               ContextControl |
                               ContextInteger |
                               ContextDebugRegisters
            };
            if (!NativeDebuggerMethods.Wow64GetThreadContext(
                    handle.DangerousGetHandle(),
                    &context))
            {
                return;
            }

            ApplyHardwareBreakpoints32(ref context, active);
            NativeDebuggerMethods.Wow64SetThreadContext(
                handle.DangerousGetHandle(),
                &context);
        }
    }

    private unsafe void ApplyHardwareBreakpoints64(
        ref NativeDebuggerMethods.Context64 context,
        BreakpointEntry[] active)
    {
        context.Dr0 = 0;
        context.Dr1 = 0;
        context.Dr2 = 0;
        context.Dr3 = 0;
        ulong dr7 = 0;
        for (int index = 0; index < active.Length; index++)
        {
            ulong address = active[index].Address;
            switch (index)
            {
                case 0:
                    context.Dr0 = address;
                    break;
                case 1:
                    context.Dr1 = address;
                    break;
                case 2:
                    context.Dr2 = address;
                    break;
                case 3:
                    context.Dr3 = address;
                    break;
            }

            dr7 |= 1UL << (index * 2);
            uint rw = active[index].Kind switch
            {
                BreakpointKind.HardwareWrite => 1u,
                BreakpointKind.HardwareRead or
                    BreakpointKind.HardwareReadWrite => 3u,
                _ => 0u
            };
            dr7 |= (ulong)rw << (16 + (index * 4));
        }

        context.Dr7 = dr7;
    }

    private unsafe void ApplyHardwareBreakpoints32(
        ref NativeDebuggerMethods.Context32 context,
        BreakpointEntry[] active)
    {
        context.Dr0 = 0;
        context.Dr1 = 0;
        context.Dr2 = 0;
        context.Dr3 = 0;
        uint dr7 = 0;
        for (int index = 0; index < active.Length; index++)
        {
            uint address = checked((uint)active[index].Address);
            switch (index)
            {
                case 0:
                    context.Dr0 = address;
                    break;
                case 1:
                    context.Dr1 = address;
                    break;
                case 2:
                    context.Dr2 = address;
                    break;
                case 3:
                    context.Dr3 = address;
                    break;
            }

            dr7 |= 1u << (index * 2);
            uint rw = active[index].Kind switch
            {
                BreakpointKind.HardwareWrite => 1u,
                BreakpointKind.HardwareRead or
                    BreakpointKind.HardwareReadWrite => 3u,
                _ => 0u
            };
            dr7 |= rw << (16 + (index * 4));
        }

        context.Dr7 = dr7;
    }

    private bool TryHandleHardwareBreakpoint(
        uint threadId,
        out bool suppressHardwareBreakpoint)
    {
        suppressHardwareBreakpoint = false;
        if (HardwareBreakpoints.Entries.Count == 0)
        {
            return false;
        }

        ulong dr6 = ReadDebugStatus(threadId);
        if (dr6 == 0 || (dr6 & 0xFUL) == 0)
        {
            return false;
        }

        int slot = System.Numerics.BitOperations.TrailingZeroCount(dr6 & 0xFUL);
        BreakpointEntry[] entries = HardwareBreakpoints.Entries.ToArray();
        if (slot < 0 || slot >= entries.Length)
        {
            ClearDebugStatus(threadId);
            return false;
        }

        BreakpointEntry breakpoint = entries[slot];
        HardwareBreakpoints.RecordHit(breakpoint.Address);
        HardwareBreakpointHit?.Invoke(breakpoint, threadId);
        ClearDebugStatus(threadId);
        if (string.IsNullOrWhiteSpace(breakpoint.Condition))
        {
            return true;
        }

        BreakpointConditionResult condition =
            _breakpointConditionEvaluator.Evaluate(
                breakpoint,
                GetRegisters(threadId));
        if (!condition.HasError && condition.ShouldBreak)
        {
            return true;
        }

        if (condition.HasError)
        {
            Diagnostic?.Invoke(
                $"Hardware breakpoint condition error @ 0x{breakpoint.Address:X}: " +
                condition.ErrorMessage);
        }
        else
        {
            HardwareBreakpoints.RecordConditionMiss(breakpoint.Address);
        }

        _temporarilySuppressedHardwareAddress = breakpoint.Address;
        ApplyHardwareBreakpoints();
        SetTrapFlag(threadId, enabled: true);
        suppressHardwareBreakpoint = true;
        return true;
    }

    private ulong ReadDebugStatus(uint threadId)
    {
        using DebugThreadHandle handle = new(
            NativeDebuggerMethods.OpenThread(
                NativeDebuggerMethods.ThreadGetContext |
                NativeDebuggerMethods.ThreadSetContext,
                inheritHandle: false,
                threadId));
        if (handle.IsInvalid)
        {
            return 0;
        }

        if (_target.Is64Bit)
        {
            unsafe
            {
                NativeDebuggerMethods.Context64 context = new()
                {
                    ContextFlags = NativeDebuggerMethods.ContextAmd64 |
                                   ContextDebugRegisters
                };
                return NativeDebuggerMethods.GetThreadContext(
                        handle.DangerousGetHandle(),
                        &context)
                    ? context.Dr6
                    : 0;
            }
        }

        unsafe
        {
            NativeDebuggerMethods.Context32 context = new()
            {
                ContextFlags = ContextI386 | ContextDebugRegisters
            };
            return NativeDebuggerMethods.Wow64GetThreadContext(
                    handle.DangerousGetHandle(),
                    &context)
                ? context.Dr6
                : 0;
        }
    }

    private void ClearDebugStatus(uint threadId)
    {
        using DebugThreadHandle handle = new(
            NativeDebuggerMethods.OpenThread(
                NativeDebuggerMethods.ThreadGetContext |
                NativeDebuggerMethods.ThreadSetContext,
                inheritHandle: false,
                threadId));
        if (handle.IsInvalid)
        {
            return;
        }

        if (_target.Is64Bit)
        {
            unsafe
            {
                NativeDebuggerMethods.Context64 context = new()
                {
                    ContextFlags = NativeDebuggerMethods.ContextAmd64 |
                                   ContextDebugRegisters
                };
                if (!NativeDebuggerMethods.GetThreadContext(
                        handle.DangerousGetHandle(),
                        &context))
                {
                    return;
                }

                context.Dr6 = 0;
                NativeDebuggerMethods.SetThreadContext(
                    handle.DangerousGetHandle(),
                    &context);
            }

            return;
        }

        unsafe
        {
            NativeDebuggerMethods.Context32 context = new()
            {
                ContextFlags = ContextI386 | ContextDebugRegisters
            };
            if (!NativeDebuggerMethods.Wow64GetThreadContext(
                    handle.DangerousGetHandle(),
                    &context))
            {
                return;
            }

            context.Dr6 = 0;
            NativeDebuggerMethods.Wow64SetThreadContext(
                handle.DangerousGetHandle(),
                &context);
        }
    }

    private DebuggerEventRecord CreateEventRecord(
        NativeDebuggerMethods.DebugEvent debugEvent)
    {
        ulong address = 0;
        uint exceptionCode = 0;
        bool firstChance = false;
        string? message = null;

        switch (debugEvent.Code)
        {
            case NativeDebuggerMethods.DebugEventCode.Exception:
                NativeDebuggerMethods.ExceptionRecord exception =
                    debugEvent.Union.Exception.ExceptionRecord;
                address = exception.ExceptionAddress;
                exceptionCode = exception.ExceptionCode;
                firstChance = debugEvent.Union.Exception.FirstChance != 0;
                break;

            case NativeDebuggerMethods.DebugEventCode.CreateProcess:
                address = unchecked((ulong)debugEvent.Union.CreateProcess.ImageBase.ToInt64());
                break;

            case NativeDebuggerMethods.DebugEventCode.LoadDll:
                address = unchecked((ulong)debugEvent.Union.LoadDll.BaseOfDll.ToInt64());
                message = TryReadDebugString(
                    debugEvent.Union.LoadDll.ImageName,
                    debugEvent.Union.LoadDll.Unicode != 0,
                    int.MaxValue);
                break;

            case NativeDebuggerMethods.DebugEventCode.UnloadDll:
                address = unchecked((ulong)debugEvent.Union.UnloadDll.BaseOfDll.ToInt64());
                break;

            case NativeDebuggerMethods.DebugEventCode.OutputDebugString:
                message = TryReadDebugString(
                    debugEvent.Union.OutputDebugString.DebugStringData,
                    debugEvent.Union.OutputDebugString.Unicode != 0,
                    debugEvent.Union.OutputDebugString.DebugStringLength);
                break;
        }

        return new DebuggerEventRecord
        {
            Kind = debugEvent.Code switch
            {
                NativeDebuggerMethods.DebugEventCode.CreateProcess => DebuggerEventKind.CreateProcess,
                NativeDebuggerMethods.DebugEventCode.ExitProcess => DebuggerEventKind.ExitProcess,
                NativeDebuggerMethods.DebugEventCode.CreateThread => DebuggerEventKind.CreateThread,
                NativeDebuggerMethods.DebugEventCode.ExitThread => DebuggerEventKind.ExitThread,
                NativeDebuggerMethods.DebugEventCode.LoadDll => DebuggerEventKind.LoadModule,
                NativeDebuggerMethods.DebugEventCode.UnloadDll => DebuggerEventKind.UnloadModule,
                NativeDebuggerMethods.DebugEventCode.OutputDebugString => DebuggerEventKind.OutputDebugString,
                NativeDebuggerMethods.DebugEventCode.Rip => DebuggerEventKind.Rip,
                _ => DebuggerEventKind.Exception
            },
            ProcessId = debugEvent.ProcessId,
            ThreadId = debugEvent.ThreadId,
            Address = address,
            ExceptionCode = exceptionCode,
            FirstChance = firstChance,
            Message = message
        };
    }

    private string? TryReadDebugString(IntPtr pointer, bool unicode, int characterCount)
    {
        if (pointer == IntPtr.Zero || characterCount == 0)
        {
            return null;
        }

        int maximumCharacters = unicode ? 16_384 : 32_768;
        int effectiveCharacterCount = characterCount == int.MaxValue
            ? maximumCharacters
            : Math.Min(characterCount, maximumCharacters);
        int byteCount = checked(effectiveCharacterCount * (unicode ? 2 : 1));
        byte[] bytes = _target.ReadBytes(unchecked((ulong)pointer.ToInt64()), byteCount);
        if (bytes.Length == 0)
        {
            return null;
        }

        if (unicode)
        {
            int length = bytes.Length / 2;
            string value = Encoding.Unicode.GetString(bytes);
            int terminator = value.IndexOf('\0');
            return terminator >= 0 ? value[..terminator] : value;
        }

        int asciiTerminator = Array.IndexOf(bytes, (byte)0);
        int asciiLength = asciiTerminator >= 0 ? asciiTerminator : bytes.Length;
        return Encoding.UTF8.GetString(bytes, 0, asciiLength);
    }

    private static void CloseDebugEventHandle(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            NativeDebuggerMethods.CloseHandle(handle);
        }
    }

    private void RaiseStateChanged()
    {
        StateChanged?.Invoke(
            this,
            new DebuggerStateChangedEventArgs
            {
                IsDebugging = _attached,
                IsPaused = _paused
            });
    }

    private async Task StopEventLoopAsync()
    {
        _shutdown.Cancel();
        if (_eventLoop is not null)
        {
            try
            {
                await _eventLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        Breakpoints.RestoreAll();
        _attached = false;
        _paused = false;
        _target.MarkDebuggerDetached();
        RaiseStateChanged();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private enum DebuggerCommandKind
    {
        Continue,
        Break,
        StepInto,
        StepOver,
        StepOut,
        Detach
    }

    private enum ResumeMode
    {
        Continue,
        StepInto,
        StepOverCall,
        StepOutReturn
    }

    private enum PostBreakpointAction
    {
        None,
        Continue,
        Pause,
        StepOverCall,
        StepOut
    }

    private readonly record struct DebuggerCommand(
        DebuggerCommandKind Kind,
        TaskCompletionSource<bool> Completion);

    private readonly record struct ResumePlan(
        uint ContinueStatus,
        ResumeMode Mode,
        uint ThreadId,
        ulong TargetAddress = 0);

    /// <summary>
    /// Minimal single-threaded synchronization context used to run the
    /// debugger event loop on one dedicated thread.
    /// </summary>
    private sealed class DebuggerLoopSynchronizationContext : SynchronizationContext
    {
        private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
        private readonly System.Collections.Concurrent.BlockingCollection<
            (SendOrPostCallback Callback, object? State)> _queue = [];

        public override void Post(SendOrPostCallback callback, object? state)
        {
            if (!_queue.IsAddingCompleted)
            {
                _queue.Add((callback, state));
            }
        }

        public override void Send(SendOrPostCallback callback, object? state)
        {
            if (Environment.CurrentManagedThreadId == _ownerThreadId)
            {
                callback(state);
                return;
            }

            Post(callback, state);
        }

        public void Complete()
        {
            if (!_queue.IsAddingCompleted)
            {
                _queue.CompleteAdding();
            }
        }

        public void Run()
        {
            foreach ((SendOrPostCallback callback, object? state) in
                     _queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        }
    }
}
