namespace DogeDebugger.Core.Modules;

public sealed class ModuleDescriptor
{
    public string Name { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public ulong BaseAddress { get; set; }

    public ulong Size { get; set; }

    public ulong EntryPoint { get; set; }

    public uint ProcessId { get; set; }

    public bool IsMainModule { get; set; }

    public bool Contains(ulong address) =>
        address >= BaseAddress && address < BaseAddress + Size;

    public string BaseAddressText => $"0x{BaseAddress:X}";

    public string EntryPointText => $"0x{EntryPoint:X}";

    public string SizeText => Size < 1024
        ? $"{Size:N0} B"
        : $"{Size / 1024d:N1} KB";
}
