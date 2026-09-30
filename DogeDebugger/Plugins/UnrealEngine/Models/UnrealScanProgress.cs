namespace DogeDebugger.Plugins.UnrealEngine.Models;

public sealed class UnrealScanProgress
{
    public string Stage { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public double Progress { get; init; }

    public bool IsIndeterminate { get; init; }
}
