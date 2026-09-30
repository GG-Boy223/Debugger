using System.Globalization;

namespace DogeDebugger.Core.Process;

public sealed record ProcessDescriptor(
    int ProcessId,
    string Name,
    string? FilePath,
    int ThreadCount,
    DateTimeOffset? StartTime,
    string? WindowTitle = null)
{
    public bool HasWindow => !string.IsNullOrWhiteSpace(WindowTitle);

    public string PidDecimal => ProcessId.ToString(CultureInfo.InvariantCulture);

    public string PidHex => $"0x{ProcessId:X}";

    public string PidDisplay => $"{PidDecimal}({PidHex})";

    public string DisplayText => HasWindow
        ? $"{PidDisplay}-{Name} - {WindowTitle}"
        : $"{PidDisplay}-{Name}";
}
