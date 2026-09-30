using System.Text.Json;

namespace DogeDebugger.Core.AI;

public sealed class McpToolResult
{
    public bool IsError { get; init; }

    public string Text { get; init; } = string.Empty;

    public JsonElement? StructuredContent { get; init; }
}
