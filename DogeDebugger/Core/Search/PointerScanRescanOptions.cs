namespace DogeDebugger.Core.Search;

public enum PointerScanRescanMode
{
    Address,
    Value
}

public sealed class PointerScanRescanOptions
{
    public PointerScanRescanMode Mode { get; set; }

    public ulong Target { get; set; }

    public int ValueSize { get; set; } = sizeof(uint);
}
