namespace DogeDebugger.Core.Search;

public sealed class SearchMatch
{
    public ulong Address { get; init; }

    public byte[] Bytes { get; init; } = [];
}
