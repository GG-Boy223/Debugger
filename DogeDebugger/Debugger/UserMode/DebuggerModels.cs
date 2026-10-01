namespace DogeDebugger.Debugger.UserMode;

public enum DebuggerPauseReason
{
    SystemBreakpoint,
    Breakpoint,
    SingleStep,
    Exception,
    UserBreak,
    EntryPoint,
    ProcessExit
}

public enum DebuggerEventKind
{
    CreateProcess,
    ExitProcess,
    CreateThread,
    ExitThread,
    LoadModule,
    UnloadModule,
    OutputDebugString,
    SystemDebuggerError,
    Exception,
    Rip
}

public sealed class RegisterSnapshot
{
    public ulong InstructionPointer { get; init; }

    public ulong StackPointer { get; init; }

    public ulong FramePointer { get; init; }

    public ulong Flags { get; init; }

    public IReadOnlyDictionary<string, ulong> GeneralPurposeRegisters { get; init; } =
        new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
}

public sealed class DebuggerEventRecord
{
    public DebuggerEventKind Kind { get; init; }

    public uint ProcessId { get; init; }

    public uint ThreadId { get; init; }

    public ulong Address { get; init; }

    public uint ExceptionCode { get; init; }

    public bool FirstChance { get; init; }

    public string? Message { get; init; }
}

public sealed class DebuggerPausedEventArgs : EventArgs
{
    public required DebuggerPauseReason Reason { get; init; }

    public required DebuggerEventRecord Event { get; init; }

    public required RegisterSnapshot Registers { get; init; }
}

public sealed class DebuggerEventEventArgs : EventArgs
{
    public required DebuggerEventRecord Event { get; init; }
}

public sealed class BreakpointFilterRequestEventArgs : EventArgs
{
    public required ulong Address { get; init; }

    public required uint ProcessId { get; init; }

    public required uint ThreadId { get; init; }

    public required ulong InstructionPointer { get; init; }
}

public sealed class InternalBreakpointHitEventArgs : EventArgs
{
    public required ulong Address { get; init; }

    public required string OwnerId { get; init; }

    public required uint ThreadId { get; init; }

    public required RegisterSnapshot Registers { get; init; }
}

public sealed class DebuggerStateChangedEventArgs : EventArgs
{
    public required bool IsDebugging { get; init; }

    public required bool IsPaused { get; init; }
}
