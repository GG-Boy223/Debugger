namespace DogeDebugger.Core.Search;

public sealed class SearchRequest
{
    public string Pattern { get; set; } = string.Empty;

    public ulong StartAddress { get; set; }

    public ulong EndAddress { get; set; } = ulong.MaxValue;

    public int MaximumResults { get; set; } = 10_000;

    public bool SearchPrivateMemory { get; set; } = true;

    public bool SearchImageMemory { get; set; } = true;

    public bool SearchMappedMemory { get; set; }
}
