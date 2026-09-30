using System.Text.Json.Serialization;

namespace DogeDebugger.Core.Trace;

public sealed class TraceStopCondition
{
    [JsonPropertyName("type")]
    public TraceStopType Type { get; set; }

    [JsonPropertyName("maxSteps")]
    public int MaxSteps { get; set; }

    [JsonPropertyName("targetRip")]
    public ulong TargetRip { get; set; }

    [JsonPropertyName("luaCondition")]
    public string LuaCondition { get; set; } = string.Empty;
}
