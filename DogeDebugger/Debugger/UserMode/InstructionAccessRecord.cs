namespace DogeDebugger.Debugger.UserMode;

public sealed record InstructionAccessRecord(
    string WatchId,
    ulong CodeAddress,
    ulong AccessAddress,
    int ValueSize,
    bool ValueReadSucceeded,
    ulong Value,
    string? MonoBaseRegisterName,
    ulong MonoBaseAddress,
    uint ThreadId,
    DateTime Timestamp);

public sealed record InstructionAccessWatchRecord(
    string WatchId,
    ulong CodeAddress,
    ulong AccessAddress,
    int ValueSize,
    bool ValueReadSucceeded,
    ulong Value,
    string? MonoBaseRegisterName,
    ulong MonoBaseAddress,
    uint LastThreadId,
    DateTime FirstSeen,
    DateTime LastSeen,
    int Count);

public enum InstructionAccessWatchState
{
    Running,
    Stopped
}

public enum InstructionAccessResumeMode
{
    WatchOnly,
    Never,
    Always
}

public sealed record InstructionAccessWatchStartResult(
    bool Success,
    string? WatchId,
    string? Error);

public sealed record InstructionAccessWatchStopResult(
    bool Success,
    string WatchId,
    bool WasRunning,
    bool PauseConfirmed,
    bool ResumeRequested,
    bool Resumed,
    InstructionAccessResumeMode ResumeMode,
    string Status,
    string? Error);

public sealed record InstructionAccessWatchSnapshot(
    string WatchId,
    int ProcessId,
    string ProcessName,
    bool Is64Bit,
    ulong CodeAddress,
    int OperandIndex,
    string OperandText,
    int DefaultValueSize,
    bool CanParseMonoInstance,
    string? MonoBaseRegisterName,
    int ValueSize,
    InstructionAccessWatchState State,
    DateTime CreatedAt,
    DateTime? StoppedAt,
    long TotalRecordCount,
    int AddressCount,
    int Offset,
    int Limit,
    IReadOnlyList<InstructionAccessWatchRecord> Records);
