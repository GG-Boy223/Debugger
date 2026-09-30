namespace DogeDebugger.Core.ExceptionHandler;

public sealed class ExceptionHandlerScanResult
{
    public IReadOnlyList<VehEntry> VehEntries { get; init; } = [];

    public IReadOnlyList<PdataSehEntry> PdataEntries { get; init; } = [];

    public IReadOnlyList<HookDetectionEntry> HookEntries { get; init; } = [];

    public bool PdataTruncated { get; init; }

    public int PdataCandidateCount { get; init; }
}
