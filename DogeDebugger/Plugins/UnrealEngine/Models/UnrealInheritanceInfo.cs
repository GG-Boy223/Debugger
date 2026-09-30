namespace DogeDebugger.Plugins.UnrealEngine.Models;

public sealed class UnrealInheritanceInfo
{
    public int Level { get; init; }

    public string Name { get; init; } = string.Empty;

    public ulong Address { get; init; }

    public string AddressHex => Address == 0 ? string.Empty : $"0x{Address:X}";
}
