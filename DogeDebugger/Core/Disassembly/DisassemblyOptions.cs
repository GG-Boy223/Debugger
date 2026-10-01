namespace DogeDebugger.Core.Disassembly;

public enum AssemblySyntax
{
    Intel = 0,
    Masm = 1,
    Nasm = 2,
    Gas = 3
}

public enum AssemblyAddressMode
{
    Absolute = 0,
    Rva = 1,
    ModuleOffset = 2
}

public enum DisassemblyBytesStyle
{
    X64Dbg = 0,
    CheatEngine = 1
}

public readonly record struct DisassemblyModuleRange(
    string Name,
    ulong BaseAddress,
    ulong Size);

public sealed class DisassemblyOptions
{
    public AssemblySyntax Syntax { get; set; } = AssemblySyntax.Intel;

    public AssemblyAddressMode AddressMode { get; set; } =
        AssemblyAddressMode.Absolute;

    public bool UppercaseHex { get; set; } = true;

    public bool ShowBytes { get; set; } = true;

    public bool ShowAddress { get; set; } = true;

    public bool ShowJumpArrows { get; set; } = true;

    public bool UseSignedImmediateOperands { get; set; }

    public DisassemblyBytesStyle BytesStyle { get; set; } =
        DisassemblyBytesStyle.X64Dbg;

    public ulong RelativeBase { get; set; }

    public string ModuleName { get; set; } = string.Empty;

    public IReadOnlyList<DisassemblyModuleRange> Modules { get; set; } = [];
}
