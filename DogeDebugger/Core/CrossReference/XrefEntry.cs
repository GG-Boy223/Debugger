namespace DogeDebugger.Core.CrossReference;

public sealed class XrefEntry
{
    public ulong FromAddress { get; init; }

    public ulong ToAddress { get; init; }

    public XrefKind Kind { get; init; }

    public string ModuleName { get; init; } = string.Empty;

    public string InstructionText { get; init; } = string.Empty;

    public string FromAddressText => $"0x{FromAddress:X}";

    public string ToAddressText => $"0x{ToAddress:X}";

    public string KindText => Kind switch
    {
        XrefKind.Call => "调用",
        XrefKind.TailCall => "尾调用",
        XrefKind.Lea => "取址",
        XrefKind.Jump => "跳转",
        XrefKind.Read => "读取",
        XrefKind.Write => "写入",
        XrefKind.Address => "地址",
        _ => Kind.ToString()
    };
}
