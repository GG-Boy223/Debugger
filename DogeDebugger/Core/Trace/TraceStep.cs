using System.Text.Json.Serialization;

namespace DogeDebugger.Core.Trace;

public sealed class TraceStep
{
    [JsonPropertyName("index")]
    public int StepIndex { get; set; }

    [JsonPropertyName("rip")]
    public ulong Rip { get; set; }

    [JsonPropertyName("asm")]
    public string? DisassemblyText { get; set; }

    [JsonPropertyName("regs")]
    public Dictionary<string, ulong> Registers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public string ModuleOffsetText { get; set; } = string.Empty;
}
