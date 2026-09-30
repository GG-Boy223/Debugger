using System.Text.Json.Serialization;

namespace DogeDebugger.Core.Trace;

public sealed class TraceSession
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("startTime")]
    public DateTime StartTime { get; set; }

    [JsonPropertyName("endTime")]
    public DateTime EndTime { get; set; }

    [JsonPropertyName("stopReason")]
    public string StopReason { get; set; } = string.Empty;

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("mode")]
    public TraceMode Mode { get; set; }

    [JsonPropertyName("condition")]
    public TraceStopCondition StopCondition { get; set; } = new();

    [JsonPropertyName("steps")]
    public List<TraceStep> Steps { get; set; } = [];
}
