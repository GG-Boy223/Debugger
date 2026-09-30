namespace DogeDebugger.Core.Signatures;

public sealed class AobSignatureOptions
{
    public int MaximumLength { get; set; } = 128;

    public int InstructionCount { get; set; } = 6;

    public bool RequireUniqueResult { get; set; } = true;

    public bool KeepModuleRelative { get; set; } = true;
}
