namespace DogeDebugger.Core.Rtti;

public sealed class RttiScanResult
{
    public IReadOnlyList<RttiTypeInfo> Types { get; init; } = [];

    public int ScannedModuleCount { get; init; }

    public int CandidateTypeCount { get; init; }

    public bool Truncated { get; init; }
}
