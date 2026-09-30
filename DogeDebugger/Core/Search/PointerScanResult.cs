namespace DogeDebugger.Core.Search;

public sealed class PointerScanResult
{
    public bool Truncated { get; init; }

    public ulong ScannedBytes { get; init; }

    public IReadOnlyList<PointerChain> Chains { get; init; } = [];
}

public sealed class PointerChain
{
    public required ulong BaseAddress { get; init; }

    public required string ModuleName { get; init; }

    public required ulong ModuleOffset { get; init; }

    public required IReadOnlyList<int> Offsets { get; init; }
}
