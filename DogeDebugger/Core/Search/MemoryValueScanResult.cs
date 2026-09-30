namespace DogeDebugger.Core.Search;

public sealed class MemoryValueScanResult
{
    public bool Truncated { get; init; }

    public ulong ScannedBytes { get; init; }

    public IReadOnlyList<MemoryValueMatch> Matches { get; init; } = [];
}
