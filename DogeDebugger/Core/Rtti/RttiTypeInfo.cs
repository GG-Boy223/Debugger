namespace DogeDebugger.Core.Rtti;

public sealed class RttiTypeInfo
{
    public string Name { get; init; } = string.Empty;

    public string DecoratedName { get; init; } = string.Empty;

    public string ModuleName { get; init; } = string.Empty;

    public string ModulePath { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public ulong TypeDescriptorAddress { get; init; }

    public ulong CompleteObjectLocatorAddress { get; init; }

    public ulong ClassHierarchyAddress { get; init; }

    public ulong BaseClassArrayAddress { get; init; }

    public ulong VftableAddress { get; init; }

    public uint Signature { get; init; }

    public uint Offset { get; init; }

    public uint ConstructorDisplacement { get; init; }

    public int BaseClassCount { get; init; }

    public IReadOnlyList<RttiBaseClassInfo> BaseClasses { get; init; } = [];

    public string TypeDescriptorText => $"0x{TypeDescriptorAddress:X}";

    public string LocatorText => $"0x{CompleteObjectLocatorAddress:X}";

    public string VftableText => VftableAddress == 0 ? "-" : $"0x{VftableAddress:X}";

    public string BaseClassText => BaseClasses.Count == 0
        ? "-"
        : string.Join(" -> ", BaseClasses.Select(baseClass => baseClass.Name));
}
