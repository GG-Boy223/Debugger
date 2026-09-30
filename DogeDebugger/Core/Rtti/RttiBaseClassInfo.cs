namespace DogeDebugger.Core.Rtti;

public sealed class RttiBaseClassInfo
{
    public string Name { get; init; } = string.Empty;

    public string DecoratedName { get; init; } = string.Empty;

    public ulong TypeDescriptorAddress { get; init; }

    public int ContainedBaseCount { get; init; }

    public int MemberDisplacement { get; init; }

    public int VtableDisplacement { get; init; }

    public int VbtableDisplacement { get; init; }

    public uint Attributes { get; init; }

    public string DisplayTypeDescriptor => $"0x{TypeDescriptorAddress:X}";
}
