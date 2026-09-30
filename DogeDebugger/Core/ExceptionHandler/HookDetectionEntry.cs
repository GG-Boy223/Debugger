namespace DogeDebugger.Core.ExceptionHandler;

public sealed class HookDetectionEntry
{
    public string FunctionName { get; set; } = string.Empty;

    public ulong Address { get; set; }

    public string ExpectedInstruction { get; set; } = string.Empty;

    public string ActualInstruction { get; set; } = string.Empty;

    public string ActualBytes { get; set; } = string.Empty;

    public bool IsHooked { get; set; }

    public string Status { get; set; } = string.Empty;

    public string DisplayAddress => $"0x{Address:X}";
}
