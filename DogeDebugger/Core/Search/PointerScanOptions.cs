namespace DogeDebugger.Core.Search;

public sealed class PointerScanOptions
{
    public ulong TargetAddress { get; set; }

    public int MaximumOffset { get; set; } = 0x1000;

    public int MaximumDepth { get; set; } = 5;

    public int Alignment { get; set; } = 4;

    public int MaximumCandidatesPerLevel { get; set; } = 100_000;

    public int MaximumResults { get; set; } = 10_000_000;

    public bool WritableOnly { get; set; } = true;

    public bool StaticOnlyBase { get; set; } = true;

    public bool IncludeMappedMemory { get; set; }

    public bool AllowNegativeOffsets { get; set; }

    public int MaximumOffsetsPerNode { get; set; } = 3;

    public bool LimitOffsetsPerNode { get; set; } = true;
}
