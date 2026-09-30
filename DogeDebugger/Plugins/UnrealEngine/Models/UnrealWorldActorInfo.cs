namespace DogeDebugger.Plugins.UnrealEngine.Models;

public sealed class UnrealWorldActorInfo
{
    public int Index { get; init; }

    public ulong Address { get; init; }

    public string Name { get; init; } = string.Empty;

    public string ClassName { get; init; } = string.Empty;

    public string PathName { get; init; } = string.Empty;

    public string AddressHex => Address == 0 ? string.Empty : $"0x{Address:X}";
}
