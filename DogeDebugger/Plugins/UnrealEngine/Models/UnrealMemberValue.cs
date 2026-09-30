namespace DogeDebugger.Plugins.UnrealEngine.Models;

public sealed class UnrealMemberValue
{
    public UnrealValueKind Kind { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public object? Value { get; set; }

    public ulong Address { get; set; }

    public string TypeName { get; set; } = string.Empty;

    public string? Error { get; set; }

    public int ChildOffset { get; set; } = -1;

    public IReadOnlyList<UnrealMemberValue> Children { get; set; } = [];

    public static UnrealMemberValue Unreadable(string error) => new()
    {
        Kind = UnrealValueKind.Unreadable,
        DisplayName = "<unreadable>",
        Error = error
    };
}
