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

    public string BaseAddressText => $"{BaseAddress:X16}";

    public string EntryPointText => $"{EntryPoint:X16}";

    public string SizeText => $"{Size:X}";
}
