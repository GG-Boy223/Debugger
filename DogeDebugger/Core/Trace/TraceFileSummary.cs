namespace DogeDebugger.Core.Trace;

public sealed class TraceFileSummary
{
    public string Id { get; set; } = string.Empty;

    public DateTime Time { get; set; }

    public int StepCount { get; set; }

    public string StopReason { get; set; } = string.Empty;

    public TraceMode Mode { get; set; }
}
