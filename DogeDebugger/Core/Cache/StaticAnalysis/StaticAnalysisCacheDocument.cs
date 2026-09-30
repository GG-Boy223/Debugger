namespace DogeDebugger.Core.Cache;

/// <summary>
/// Fully deserialized static-analysis cache document written by the original
/// analyzer. The nested collections mirror the on-disk schema exactly; fields
/// whose semantic role is not yet proven keep a neutral name and are
/// documented as schema slots.
/// </summary>
public sealed class StaticAnalysisCacheDocument
{
    public required ushort Version { get; init; }

    public required string ModuleName { get; init; }

    public required string ModulePath { get; init; }

    public required ulong BaseAddress { get; init; }

    public required uint ImageSize { get; init; }

    public required uint EntryPointRva { get; init; }

    public required StaticAnalysisMachine Machine { get; init; }

    public required string FileHash { get; init; }

    public required DateTime AnalysisTime { get; init; }

    public required TimeSpan Duration { get; init; }

    public required IReadOnlyList<StaticAnalysisSection> Sections { get; init; }

    public required StaticAnalysisFunctionAnalysis Functions { get; init; }

    public required StaticAnalysisCrossReferenceAnalysis CrossReferences { get; init; }

    public required StaticAnalysisStringAnalysis Strings { get; init; }

    public StaticAnalysisSemanticSummary? Semantics { get; init; }
}

public sealed class StaticAnalysisSection
{
    public required string Name { get; init; }

    public required uint VirtualAddress { get; init; }

    public required uint VirtualSize { get; init; }

    public required uint RawOffset { get; init; }

    public required uint RawSize { get; init; }

    public required uint Characteristics { get; init; }
}

public sealed class StaticAnalysisFunctionAnalysis
{
    public required string Name { get; init; }

    public required StaticAnalysisMachine Machine { get; init; }

    public required ulong BaseAddress { get; init; }

    public required uint ImageSize { get; init; }

    public required uint EntryPointRva { get; init; }

    public required IReadOnlyList<uint> Counters { get; init; }

    public required IReadOnlyList<StaticAnalysisFunction> Functions { get; init; }

    public required IReadOnlyList<StaticAnalysisFlowRecord> FlowRecords { get; init; }
}

public sealed class StaticAnalysisFunction
{
    public required uint StartRva { get; init; }

    public required uint RelatedRva1 { get; init; }

    public required uint RelatedRva2 { get; init; }

    public required uint MinBlockRva { get; init; }

    public required uint MaxBlockEndRva { get; init; }

    public required uint InstructionCount { get; init; }

    public required uint FlowEdgeCount { get; init; }

    /// <summary>
    /// Schema slot 8 of the function record. Real analyzer output commonly
    /// stores 0 here while block ranges are present, so this is not the block
    /// count; the number of block ranges is <c>BlockRanges.Count</c>.
    /// </summary>
    public required uint AnalysisCounter { get; init; }

    public required uint PrimaryMetric { get; init; }

    public required ulong Flags { get; init; }

    public required ulong ExtendedFlags { get; init; }

    public required byte Classification { get; init; }

    public required IReadOnlyList<uint> BlockStartRvas { get; init; }

    public required IReadOnlyList<uint> SuccessorRvas { get; init; }

    public required IReadOnlyList<uint> InstructionRvas { get; init; }

    public required IReadOnlyList<uint> IncomingRvas { get; init; }

    public required IReadOnlyList<string> NameHints { get; init; }

    public required IReadOnlyList<StaticAnalysisRange> BlockRanges { get; init; }

    public required IReadOnlyList<StaticAnalysisFlowEdge> FlowEdges { get; init; }

    public required IReadOnlyList<StaticAnalysisFunctionDetail> Details { get; init; }
}

public sealed class StaticAnalysisRange
{
    public required uint StartRva { get; init; }

    public required uint EndRva { get; init; }
}

public sealed class StaticAnalysisFlowEdge
{
    public required uint SourceStartRva { get; init; }

    public required uint SourceEndRva { get; init; }

    public required uint KindValue { get; init; }

    public required byte FlowKind { get; init; }

    public required IReadOnlyList<uint> ConditionValues { get; init; }

    public required uint TargetRva { get; init; }
}

public sealed class StaticAnalysisFunctionDetail
{
    public required uint Value1 { get; init; }

    public required uint Value2 { get; init; }

    public required byte Value3 { get; init; }

    public required string Text { get; init; }

    public required IReadOnlyList<StaticAnalysisRange> Ranges { get; init; }

    public required IReadOnlyList<uint> Values { get; init; }
}

public sealed class StaticAnalysisFlowRecord
{
    public required uint SourceRva { get; init; }

    public required uint TargetRva { get; init; }

    public required ulong Data { get; init; }

    public required uint KindValue { get; init; }
}

public sealed class StaticAnalysisCrossReferenceAnalysis
{
    public required string Name { get; init; }

    public required StaticAnalysisMachine Machine { get; init; }

    public required ulong BaseAddress { get; init; }

    public required uint ImageSize { get; init; }

    public required uint EntryPointRva { get; init; }

    public required IReadOnlyList<uint> Counters { get; init; }

    public required IReadOnlyList<StaticAnalysisCrossReference> CrossReferences { get; init; }
}

public sealed class StaticAnalysisCrossReference
{
    public required uint SourceRva { get; init; }

    public required uint TargetRva { get; init; }

    public required uint SourceFunctionRva { get; init; }

    public required uint TargetFunctionRva { get; init; }

    public required byte KindValue { get; init; }

    public required byte FlowKind { get; init; }

    public required byte Flags { get; init; }

    public required bool IsDirect { get; init; }

    public required bool IsCall { get; init; }

    public required bool IsJump { get; init; }

    public required bool IsData { get; init; }

    public required string SourceText { get; init; }

    public required string TargetText { get; init; }

    public required string Details { get; init; }
}

public sealed class StaticAnalysisStringAnalysis
{
    public required string Name { get; init; }

    public required StaticAnalysisMachine Machine { get; init; }

    public required ulong BaseAddress { get; init; }

    public required uint ImageSize { get; init; }

    public required IReadOnlyList<uint> Counters { get; init; }

    public required IReadOnlyList<StaticAnalysisStringEntry> Strings { get; init; }
}

public sealed class StaticAnalysisStringEntry
{
    public required uint Rva { get; init; }

    public required uint Length { get; init; }

    public required uint ContainingFunctionRva { get; init; }

    public required byte KindValue { get; init; }

    public required uint Flags { get; init; }

    public required bool IsUnicode { get; init; }

    public required bool IsReferenced { get; init; }

    public required bool IsInDataSection { get; init; }

    public required bool IsWide { get; init; }

    public required string Value { get; init; }

    public required string SourceText { get; init; }

    public required string Details { get; init; }

    public required uint ReferenceCount { get; init; }

    public required uint ByteLength { get; init; }

    public required uint CharacterCount { get; init; }

    public required uint Flags2 { get; init; }

    public required IReadOnlyList<uint> ReferenceRvas { get; init; }
}

public sealed class StaticAnalysisSemanticSummary
{
    public required string Name { get; init; }

    public required StaticAnalysisMachine Machine { get; init; }

    public required ulong BaseAddress { get; init; }

    public required uint ImageSize { get; init; }

    public required uint EntryPointRva { get; init; }

    public required IReadOnlyList<uint> Counters { get; init; }

    public required IReadOnlyList<StaticAnalysisSemanticEntry> Entries { get; init; }

    public required IReadOnlyList<StaticAnalysisSemanticType> Types { get; init; }
}

public sealed class StaticAnalysisSemanticEntry
{
    public required string PrimaryText { get; init; }

    public required string SecondaryText { get; init; }

    public required string Details { get; init; }

    public required uint Rva { get; init; }
}

public sealed class StaticAnalysisSemanticType
{
    public required uint Value1 { get; init; }

    public required uint Value2 { get; init; }

    public required uint Value3 { get; init; }

    public required uint Value4 { get; init; }

    public required uint Value5 { get; init; }

    public required uint Value6 { get; init; }

    public required uint Value7 { get; init; }

    public required string Name { get; init; }

    public required IReadOnlyList<StaticAnalysisRange> Ranges { get; init; }

    public required IReadOnlyList<string> Texts { get; init; }

    public required IReadOnlyList<uint> Counters { get; init; }

    public required bool Flag { get; init; }

    public required IReadOnlyList<StaticAnalysisSemanticMember> Members { get; init; }

    public required uint MemberCount { get; init; }
}

public sealed class StaticAnalysisSemanticMember
{
    public required string Name { get; init; }

    public required uint Value1 { get; init; }

    public required uint Value2 { get; init; }

    public required string TypeName { get; init; }

    public required string DeclaringTypeName { get; init; }

    public required string Details { get; init; }
}
