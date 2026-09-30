namespace DogeDebugger.Plugins.UnrealEngine.Models;

public sealed class UnrealMemberValueEntry
{
    public required UnrealMemberInfo Member { get; init; }

    public required UnrealMemberValue Value { get; init; }

    public string TypeName { get; init; } = string.Empty;
}
