namespace DogeDebugger.Core.Disassembly;

public enum AssemblySyntax
{
    Intel = 0,
    Masm = 1,
    Nasm = 2,
    Gas = 3
}

public sealed class DisassemblyOptions
{
    public AssemblySyntax Syntax { get; set; } = AssemblySyntax.Intel;

    public bool UppercaseHex { get; set; } = true;

    public bool ShowBytes { get; set; } = true;

    public bool ShowAddress { get; set; } = true;
}
