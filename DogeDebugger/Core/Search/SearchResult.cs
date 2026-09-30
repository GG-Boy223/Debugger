namespace DogeDebugger.Core.Search;

public sealed class SearchResult
{
    public bool Truncated { get; init; }

    public ulong ScannedBytes { get; init; }

    public IReadOnlyList<SearchMatch> Matches { get; init; } = [];
}
