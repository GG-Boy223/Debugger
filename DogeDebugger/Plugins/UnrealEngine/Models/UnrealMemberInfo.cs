namespace DogeDebugger.Plugins.UnrealEngine.Models;

public sealed class UnrealMemberInfo
{
    public string Source { get; init; } = string.Empty;

    public ulong Address { get; init; }

    public string Name { get; init; } = string.Empty;

    public string TypeName { get; init; } = string.Empty;

    public int Offset { get; init; } = -1;

    public int Size { get; init; }

    public int ArrayDim { get; init; }

    public ulong Flags { get; init; }

    public ulong NativeAddress { get; init; }

    public string DeclaringType { get; init; } = string.Empty;

    public bool IsInherited => !string.IsNullOrWhiteSpace(DeclaringType);

    public string AddressHex => Address == 0 ? string.Empty : $"0x{Address:X}";

    public string OffsetHex => Offset < 0 ? string.Empty : $"0x{Offset:X}";

    public string SizeText => Size <= 0 ? string.Empty : $"0x{Size:X}";

    public string FlagsHex => Flags == 0 ? string.Empty : $"0x{Flags:X}";

    public string NativeAddressHex => NativeAddress == 0
        ? string.Empty
        : $"0x{NativeAddress:X}";
}
