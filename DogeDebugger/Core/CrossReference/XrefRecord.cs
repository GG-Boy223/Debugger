namespace DogeDebugger.Core.CrossReference;

public readonly record struct XrefRecord(
    uint SourceRva,
    uint TargetRva,
    XrefKind Kind);
