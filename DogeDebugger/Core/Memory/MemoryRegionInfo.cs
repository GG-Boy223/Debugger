namespace DogeDebugger.Core.Memory;

public sealed class MemoryRegionInfo
{
    public ulong BaseAddress { get; set; }

    public ulong AllocationBase { get; set; }

    public ulong Size { get; set; }

    public uint State { get; set; }

    public uint Type { get; set; }

    public uint AllocationProtect { get; set; }

    public uint Protect { get; set; }

    public string AllocationProtectText { get; set; } = string.Empty;

    public string ProtectText { get; set; } = string.Empty;

    public string ModuleName { get; set; } = string.Empty;

    public string SectionName { get; set; } = string.Empty;

    public bool IsModuleHeader { get; set; }

    public bool IsReadable =>
        (Protect & 0xEE) != 0 &&
        (Protect & 0x101) == 0;

    public bool IsWritable =>
        (Protect & 0xCC) != 0 &&
        (Protect & 0x101) == 0;

    public bool IsExecutable =>
        (Protect & 0xF0) != 0 &&
        (Protect & 0x101) == 0;

    public string BaseAddressText => $"0x{BaseAddress:X}";

    public string AllocationBaseText => $"0x{AllocationBase:X}";

    public string SizeText => Size < 1024
        ? $"{Size:N0} B"
        : Size < 1024 * 1024
            ? $"{Size / 1024d:N1} KB"
            : $"{Size / (1024d * 1024d):N1} MB";

    public string StateText => State switch
    {
        0x1000 => "Commit",
        0x2000 => "Reserve",
        0x10000 => "Free",
        _ => $"0x{State:X}"
    };

    public string TypeText => Type switch
    {
        0x1000000 => "Image",
        0x40000 => "Mapped",
        0x20000 => "Private",
        _ => $"0x{Type:X}"
    };
}
