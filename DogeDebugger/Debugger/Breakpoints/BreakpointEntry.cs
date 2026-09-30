namespace DogeDebugger.Debugger.Breakpoints;

public sealed class BreakpointEntry
{
    public required ulong Address { get; init; }

    public BreakpointKind Kind { get; init; } = BreakpointKind.Software;

    public bool IsEnabled { get; internal set; }

    public bool IsArmed { get; internal set; }

    public bool IsTemporary { get; init; }

    public byte OriginalByte { get; internal set; }

    public long HitCount { get; internal set; }

    public string Comment { get; set; } = string.Empty;

    public string ModuleName { get; internal set; } = string.Empty;

    public string SymbolName { get; internal set; } = string.Empty;

    public string Condition { get; set; } = string.Empty;

    public int ConditionMissCount { get; internal set; }

    public string AddressText => $"0x{Address:X}";

    public string KindText => Kind switch
    {
        BreakpointKind.Software => "软件",
        BreakpointKind.HardwareExecute => "硬件执行",
        BreakpointKind.HardwareRead => "硬件读取",
        BreakpointKind.HardwareWrite => "硬件写入",
        BreakpointKind.HardwareReadWrite => "硬件读写",
        _ => Kind.ToString()
    };

    public string StateText => IsEnabled
        ? IsArmed ? "已启用" : "待恢复"
        : "已禁用";

    public bool IsActive => IsEnabled;
}
