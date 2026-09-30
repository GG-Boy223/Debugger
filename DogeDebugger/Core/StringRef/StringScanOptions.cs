namespace DogeDebugger.Core.StringRef;

public sealed class StringScanOptions
{
    public int MinimumLength { get; set; } = 4;

    public int MaximumLength { get; set; } = 4096;

    public int MaximumResults { get; set; } = 100_000;

    public bool ScanAsciiUtf8 { get; set; } = true;

    public bool ScanUtf16Le { get; set; } = true;

    public bool IncludeImageMemory { get; set; } = true;

    public bool IncludePrivateMemory { get; set; } = true;

    public bool IncludeMappedMemory { get; set; }
}
