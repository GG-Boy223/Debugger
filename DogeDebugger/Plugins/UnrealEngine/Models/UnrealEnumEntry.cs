namespace DogeDebugger.Plugins.UnrealEngine.Models;

public sealed class UnrealEnumEntry
{
    public required string Name { get; init; }

    public required string ShortName { get; init; }

    public long Value { get; init; }
}
