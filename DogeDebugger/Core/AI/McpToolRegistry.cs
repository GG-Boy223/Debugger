using System.Collections.Concurrent;

namespace DogeDebugger.Core.AI;

public sealed class McpToolRegistry
{
    private readonly ConcurrentDictionary<string, McpToolDefinition> _tools =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<McpToolDefinition> Tools =>
        _tools.Values.OrderBy(static tool => tool.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public void Register(McpToolDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
        if (!_tools.TryAdd(definition.Name, definition))
        {
            throw new InvalidOperationException($"MCP tool '{definition.Name}' is already registered.");
        }
    }

    public bool Remove(string name) => _tools.TryRemove(name, out _);

    public bool TryGet(string name, out McpToolDefinition? definition) =>
        _tools.TryGetValue(name, out definition);
}
