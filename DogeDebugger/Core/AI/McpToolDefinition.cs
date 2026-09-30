using System.Text.Json;

namespace DogeDebugger.Core.AI;

public sealed class McpToolDefinition
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required JsonElement InputSchema { get; init; }

    public required Func<JsonElement, CancellationToken, ValueTask<McpToolResult>> Handler { get; init; }
}
