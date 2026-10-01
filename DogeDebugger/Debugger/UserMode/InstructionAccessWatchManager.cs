using System.Collections.Concurrent;
using DogeDebugger.Core.Disassembly;
using DogeDebugger.Core.Process;
using DogeDebugger.Debugger.Breakpoints;

namespace DogeDebugger.Debugger.UserMode;

public sealed class InstructionAccessWatchManager : IDisposable
{
    private const int MinimumRecordLimit = 1;
    private const int MaximumRecordLimit = 100_000;
    private const int DrainIntervalMilliseconds = 50;

    private readonly ITargetProcess _target;
    private readonly UserModeDebugger _debugger;
    private readonly ConcurrentDictionary<string, WatchRegistration> _watches =
        new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<InstructionAccessRecord> _pendingRecords = new();
    private readonly SemaphoreSlim _drainGate = new(1, 1);
    private readonly Timer _drainTimer;
    private bool _disposed;

    public InstructionAccessWatchManager(
        ITargetProcess target,
        UserModeDebugger debugger)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _debugger = debugger ?? throw new ArgumentNullException(nameof(debugger));
        _drainTimer = new Timer(
            static state => ((InstructionAccessWatchManager)state!).DrainPendingRecords(),
            this,
            Timeout.Infinite,
            Timeout.Infinite);
        _debugger.InternalBreakpointHit += HandleInternalBreakpointHit;
        _debugger.Paused += HandlePaused;
    }

    public event Action<InstructionAccessWatchSnapshot>? Updated;

    public event Action<string>? StateChanged;

    public IReadOnlyList<InstructionAccessWatchSnapshot> GetSnapshots(
        int offset = 0,
        int limit = 0)
    {
        ThrowIfDisposed();
        int safeOffset = Math.Max(offset, 0);
        int safeLimit = limit == 0
            ? int.MaxValue
            : Math.Clamp(limit, 0, 10_000);
        return _watches.Values
            .OrderByDescending(static watch => watch.CreatedAt)
            .Skip(safeOffset)
            .Take(safeLimit)
            .Select(static watch => watch)
            .Select(watch => CreateSnapshot(watch))
            .ToArray();
    }

    public bool TryGetSnapshot(
        string watchId,
        int offset,
        int limit,
        out InstructionAccessWatchSnapshot? snapshot)
    {
        ThrowIfDisposed();
        if (!_watches.TryGetValue(watchId, out WatchRegistration? registration))
        {
            snapshot = null;
            return false;
        }

        snapshot = CreateSnapshot(
            registration,
            Math.Max(offset, 0),
            Math.Clamp(limit, 0, 10_000));
        return true;
    }

    public InstructionAccessWatchStartResult Start(
        InstructionSnapshot instruction,
        InstructionMemoryOperand operand,
        int bitness,
        int? requestedValueSize,
        int maximumRecords)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(instruction);
        ArgumentNullException.ThrowIfNull(operand);

        if (!_target.IsOpen || !_debugger.IsDebugging)
        {
            return new InstructionAccessWatchStartResult(
                false,
                null,
                "Attach the debugger before starting an instruction access watch.");
        }

        if (_watches.Values.Any(watch =>
                watch.State == InstructionAccessWatchState.Running &&
                watch.CodeAddress == instruction.Address))
        {
            return new InstructionAccessWatchStartResult(
                false,
                null,
                "The instruction already has an active access watch.");
        }

        int valueSize = NormalizeValueSize(
            requestedValueSize ?? operand.DefaultValueSize);
        WatchRegistration registration = new(
            $"ia_{Guid.NewGuid():N}",
            _target.ProcessId,
            _target.ProcessName,
            _target.Is64Bit,
            instruction.Address,
            instruction,
            operand.OperandIndex,
            operand.DisplayText,
            operand.DefaultValueSize,
            operand.CanParseMonoInstance,
            operand.MonoBaseRegister,
            bitness,
            valueSize,
            Math.Clamp(maximumRecords, MinimumRecordLimit, MaximumRecordLimit));

        if (!_watches.TryAdd(registration.WatchId, registration))
        {
            return new InstructionAccessWatchStartResult(
                false,
                null,
                "Failed to allocate an instruction access watch.");
        }

        byte? originalByte = instruction.Bytes.Length > 0
            ? instruction.Bytes[0]
            : null;
        if (!_debugger.Breakpoints.Add(
                instruction.Address,
                temporary: false,
                originalByte))
        {
            _watches.TryRemove(registration.WatchId, out _);
            return new InstructionAccessWatchStartResult(
                false,
                null,
                "Failed to set the internal instruction breakpoint. " +
                "The address may already have a breakpoint or watch.");
        }

        if (!_debugger.Breakpoints.MarkInternal(
                instruction.Address,
                registration.WatchId))
        {
            _debugger.Breakpoints.Remove(instruction.Address);
            _watches.TryRemove(registration.WatchId, out _);
            return new InstructionAccessWatchStartResult(
                false,
                null,
                "Failed to register the internal instruction access watch.");
        }

        lock (registration.SyncRoot)
        {
            registration.BreakpointOwned = true;
            registration.State = InstructionAccessWatchState.Running;
        }

        _drainTimer.Change(0, DrainIntervalMilliseconds);
        _debugger.ReportDiagnostic(
            $"InstructionAccessWatch start {registration.WatchId} " +
            $"code=0x{registration.CodeAddress:X} operand={registration.OperandIndex} " +
            $"size={registration.ValueSize}");
        StateChanged?.Invoke(registration.WatchId);
        return new InstructionAccessWatchStartResult(
            true,
            registration.WatchId,
            null);
    }

    public bool ChangeValueSize(string watchId, int valueSize, out string error)
    {
        ThrowIfDisposed();
        if (!_watches.TryGetValue(watchId, out WatchRegistration? registration))
        {
            error = "The instruction access watch was not found.";
            return false;
        }

        if (valueSize is not (1 or 2 or 4 or 8))
        {
            error = "The value size must be 1, 2, 4, or 8 bytes.";
            return false;
        }

        lock (registration.SyncRoot)
        {
            if (registration.State != InstructionAccessWatchState.Running)
            {
                error = "The instruction access watch has already stopped.";
                return false;
            }

            if (registration.ValueSize == valueSize)
            {
                error = string.Empty;
                return true;
            }

            registration.ValueSize = valueSize;
            registration.Records.Clear();
        }

        Publish(registration);
        error = string.Empty;
        return true;
    }

    public async Task<InstructionAccessWatchStopResult> StopAsync(
        string watchId,
        InstructionAccessResumeMode resumeMode,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_watches.TryGetValue(watchId, out WatchRegistration? registration))
        {
            return new InstructionAccessWatchStopResult(
                false,
                watchId,
                false,
                false,
                false,
                false,
                resumeMode,
                "watch_not_found",
                "The instruction access watch was not found.");
        }

        bool wasRunning;
        lock (registration.SyncRoot)
        {
            wasRunning = registration.State == InstructionAccessWatchState.Running;
            registration.State = InstructionAccessWatchState.Stopped;
            registration.StoppedAt ??= DateTime.UtcNow;
        }

        bool pauseConfirmed = false;

        bool breakpointRemoved = true;
        if (registration.BreakpointOwned)
        {
            breakpointRemoved = _debugger.Breakpoints.Remove(registration.CodeAddress);
            if (!breakpointRemoved)
            {
                lock (registration.SyncRoot)
                {
                    registration.State = wasRunning
                        ? InstructionAccessWatchState.Running
                        : InstructionAccessWatchState.Stopped;
                    registration.StoppedAt = wasRunning ? null : registration.StoppedAt;
                }

                Publish(registration);
                return new InstructionAccessWatchStopResult(
                    false,
                    watchId,
                    wasRunning,
                    pauseConfirmed,
                    false,
                    false,
                    resumeMode,
                    "breakpoint_remove_failed",
                    "Failed to remove the internal instruction breakpoint.");
            }

            registration.BreakpointOwned = false;
        }

        bool resumeRequested =
            resumeMode == InstructionAccessResumeMode.Always ||
            (resumeMode == InstructionAccessResumeMode.WatchOnly && pauseConfirmed);
        bool resumed = false;
        if (resumeRequested && _debugger.IsDebugging && _debugger.IsPaused)
        {
            resumed = await _debugger.ContinueAsync(cancellationToken)
                .ConfigureAwait(false);
            if (resumed)
            {
            }
        }

        string status = resumeRequested
            ? resumed ? "resumed" : "resume_not_confirmed"
            : "stopped";
        Publish(registration);
        StateChanged?.Invoke(watchId);
        return new InstructionAccessWatchStopResult(
            true,
            watchId,
            wasRunning,
            pauseConfirmed,
            resumeRequested,
            resumed,
            resumeMode,
            status,
            null);
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        string[] watchIds = _watches.Keys.ToArray();
        foreach (string watchId in watchIds)
        {
            await StopAsync(
                    watchId,
                    InstructionAccessResumeMode.WatchOnly,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        _drainTimer.Change(Timeout.Infinite, Timeout.Infinite);
        while (_pendingRecords.TryDequeue(out _))
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _debugger.InternalBreakpointHit -= HandleInternalBreakpointHit;
        _debugger.Paused -= HandlePaused;
        _drainTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _drainTimer.Dispose();
        foreach (WatchRegistration registration in _watches.Values)
        {
            if (registration.BreakpointOwned)
            {
                _debugger.Breakpoints.Remove(registration.CodeAddress);
                registration.BreakpointOwned = false;
            }
        }

        _watches.Clear();
        while (_pendingRecords.TryDequeue(out _))
        {
        }

        _drainGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private bool HandleInternalBreakpointHit(
        InternalBreakpointHitEventArgs eventArgs)
    {
        if (!_watches.TryGetValue(
                eventArgs.OwnerId,
                out WatchRegistration? registration) ||
            registration.State != InstructionAccessWatchState.Running ||
            registration.CodeAddress != eventArgs.Address)
        {
            _debugger.ReportDiagnostic(
                $"InstructionAccessWatch hit ignored owner={eventArgs.OwnerId} " +
                $"address=0x{eventArgs.Address:X}");
            return false;
        }

        if (!InstructionMemoryOperandBuilder.TryResolveAddress(
                registration.Instruction,
                registration.Bitness,
                registration.OperandIndex,
                eventArgs.Registers.GeneralPurposeRegisters,
                out ulong accessAddress))
        {
            _debugger.ReportDiagnostic(
                $"InstructionAccessWatch resolve failed {registration.WatchId} " +
                $"code=0x{registration.CodeAddress:X} operand={registration.OperandIndex}");
            return false;
        }

        int valueSize = registration.ValueSize;
        byte[] buffer = new byte[valueSize];
        bool valueReadSucceeded = _target.TryReadBytes(
            accessAddress,
            buffer);
        ulong value = valueReadSucceeded
            ? ReadValue(buffer, valueSize)
            : 0;

        string? monoBaseRegisterName = null;
        ulong monoBaseAddress = 0;
        if (registration.CanParseMonoInstance &&
            InstructionMemoryOperandBuilder.TryGetRegisterValue(
                registration.MonoBaseRegister,
                eventArgs.Registers.GeneralPurposeRegisters,
                out ulong registerValue))
        {
            monoBaseRegisterName = registration.MonoBaseRegister
                .ToString()
                .ToUpperInvariant();
            monoBaseAddress = registerValue;
        }

        InstructionAccessRecord record = new(
            registration.WatchId,
            registration.CodeAddress,
            accessAddress,
            valueSize,
            valueReadSucceeded,
            value,
            monoBaseRegisterName,
            monoBaseAddress,
            eventArgs.ThreadId,
            DateTime.UtcNow);
        lock (registration.SyncRoot)
        {
            registration.TotalRecordCount++;
        }

        _pendingRecords.Enqueue(record);
        _debugger.ReportDiagnostic(
            $"InstructionAccessWatch record {registration.WatchId} " +
            $"address=0x{accessAddress:X} value=0x{value:X} size={valueSize} " +
            $"thread={eventArgs.ThreadId}");
        return true;
    }

    private void HandlePaused(
        object? sender,
        DebuggerPausedEventArgs eventArgs)
    {
        if (eventArgs.Reason != DebuggerPauseReason.Breakpoint ||
            !_debugger.Breakpoints.TryResolveHit(
                eventArgs.Event.Address,
                out BreakpointEntry? breakpoint) ||
            breakpoint is not { IsInternal: true })
        {
            return;
        }

        _ = _debugger.ContinueAsync();
    }

    private void DrainPendingRecords()
    {
        if (_disposed || !_drainGate.Wait(0))
        {
            return;
        }

        try
        {
            HashSet<string> changed = new(StringComparer.Ordinal);
            while (_pendingRecords.TryDequeue(out InstructionAccessRecord? record))
            {
                if (!_watches.TryGetValue(
                        record.WatchId,
                        out WatchRegistration? registration))
                {
                    continue;
                }

                lock (registration.SyncRoot)
                {
                    if (!registration.Records.TryGetValue(
                            record.AccessAddress,
                            out MutableRecord? aggregate))
                    {
                        if (registration.Records.Count >= registration.MaximumRecords)
                        {
                            ulong oldestAddress = registration.Records
                                .OrderBy(static pair => pair.Value.LastSeen)
                                .ThenBy(static pair => pair.Key)
                                .First()
                                .Key;
                            registration.Records.Remove(oldestAddress);
                        }

                        aggregate = new MutableRecord
                        {
                            FirstSeen = record.Timestamp
                        };
                        registration.Records.Add(
                            record.AccessAddress,
                            aggregate);
                    }

                    aggregate.Count++;
                    aggregate.ValueSize = record.ValueSize;
                    aggregate.ValueReadSucceeded = record.ValueReadSucceeded;
                    aggregate.Value = record.Value;
                    aggregate.MonoBaseRegisterName = record.MonoBaseRegisterName;
                    aggregate.MonoBaseAddress = record.MonoBaseAddress;
                    aggregate.LastThreadId = record.ThreadId;
                    aggregate.LastSeen = record.Timestamp;
                    changed.Add(record.WatchId);
                }
            }

            foreach (string watchId in changed)
            {
                if (_watches.TryGetValue(watchId, out WatchRegistration? registration))
                {
                    Publish(registration);
                }
            }
        }
        finally
        {
            _drainGate.Release();
        }
    }

    private InstructionAccessWatchSnapshot CreateSnapshot(
        WatchRegistration registration,
        int offset = 0,
        int limit = 0)
    {
        lock (registration.SyncRoot)
        {
            int safeOffset = Math.Max(offset, 0);
            int safeLimit = limit == 0
                ? int.MaxValue
                : Math.Clamp(limit, 0, 10_000);
            List<InstructionAccessWatchRecord> records = registration.Records
                .OrderByDescending(static pair => pair.Value.LastSeen)
                .ThenBy(static pair => pair.Key)
                .Skip(safeOffset)
                .Take(safeLimit)
                .Select(pair => pair.Value.ToRecord(
                    registration.WatchId,
                    registration.CodeAddress,
                    pair.Key))
                .ToList();
            return new InstructionAccessWatchSnapshot(
                registration.WatchId,
                registration.ProcessId,
                registration.ProcessName,
                registration.Is64Bit,
                registration.CodeAddress,
                registration.OperandIndex,
                registration.OperandText,
                registration.DefaultValueSize,
                registration.CanParseMonoInstance,
                registration.MonoBaseRegister == Iced.Intel.Register.None
                    ? null
                    : registration.MonoBaseRegister.ToString().ToUpperInvariant(),
                registration.ValueSize,
                registration.State,
                registration.CreatedAt,
                registration.StoppedAt,
                registration.TotalRecordCount,
                registration.Records.Count,
                safeOffset,
                safeLimit,
                records);
        }
    }

    private void Publish(WatchRegistration registration)
    {
        try
        {
            Updated?.Invoke(CreateSnapshot(registration));
        }
        catch
        {
        }
    }

    private static int NormalizeValueSize(int requested) =>
        requested is 1 or 2 or 4 or 8 ? requested : 8;

    private static ulong ReadValue(byte[] buffer, int size) =>
        size switch
        {
            1 => buffer[0],
            2 => BitConverter.ToUInt16(buffer),
            4 => BitConverter.ToUInt32(buffer),
            8 => BitConverter.ToUInt64(buffer),
            _ => 0
        };

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class WatchRegistration
    {
        public WatchRegistration(
            string watchId,
            int processId,
            string processName,
            bool is64Bit,
            ulong codeAddress,
            InstructionSnapshot instruction,
            int operandIndex,
            string operandText,
            int defaultValueSize,
            bool canParseMonoInstance,
            Iced.Intel.Register monoBaseRegister,
            int bitness,
            int valueSize,
            int maximumRecords)
        {
            WatchId = watchId;
            ProcessId = processId;
            ProcessName = processName;
            Is64Bit = is64Bit;
            CodeAddress = codeAddress;
            OperandIndex = operandIndex;
            OperandText = operandText;
            DefaultValueSize = defaultValueSize;
            CanParseMonoInstance = canParseMonoInstance;
            MonoBaseRegister = monoBaseRegister;
            Bitness = bitness;
            ValueSize = valueSize;
            MaximumRecords = maximumRecords;
            Instruction = instruction;
        }

        public object SyncRoot { get; } = new();

        public string WatchId { get; }

        public int ProcessId { get; }

        public string ProcessName { get; }

        public bool Is64Bit { get; }

        public ulong CodeAddress { get; }

        public int OperandIndex { get; }

        public string OperandText { get; }

        public int DefaultValueSize { get; }

        public bool CanParseMonoInstance { get; }

        public Iced.Intel.Register MonoBaseRegister { get; }

        public int Bitness { get; }

        public int ValueSize { get; set; }

        public int MaximumRecords { get; }

        public InstructionSnapshot Instruction { get; }

        public bool BreakpointOwned { get; set; }

        public InstructionAccessWatchState State { get; set; } =
            InstructionAccessWatchState.Running;

        public DateTime CreatedAt { get; } = DateTime.UtcNow;

        public DateTime? StoppedAt { get; set; }

        public long TotalRecordCount { get; set; }

        public Dictionary<ulong, MutableRecord> Records { get; } = [];
    }

    private sealed class MutableRecord
    {
        public int Count { get; set; }

        public int ValueSize { get; set; }

        public bool ValueReadSucceeded { get; set; }

        public ulong Value { get; set; }

        public string? MonoBaseRegisterName { get; set; }

        public ulong MonoBaseAddress { get; set; }

        public uint LastThreadId { get; set; }

        public DateTime FirstSeen { get; set; }

        public DateTime LastSeen { get; set; }

        public InstructionAccessWatchRecord ToRecord(
            string watchId,
            ulong codeAddress,
            ulong accessAddress) =>
            new(
                watchId,
                codeAddress,
                accessAddress,
                ValueSize,
                ValueReadSucceeded,
                Value,
                MonoBaseRegisterName,
                MonoBaseAddress,
                LastThreadId,
                FirstSeen,
                LastSeen,
                Count);
    }
}
