namespace DogeDebugger.Core.Signatures.CrossVersion;

public enum CrossVersionTargetKind
{
    Auto,
    Function,
    FunctionInterior,
    GlobalData,
    String,
    Unknown
}

public enum CrossVersionResolutionState
{
    Unresolved,
    Resolved,
    Ambiguous,
    Failed
}

public enum CrossVersionEvidenceKind
{
    BytePattern,
    FunctionBoundary,
    ExportSymbol,
    CrossReference,
    StringReference,
    ImportReference,
    LiveMemory
}

public sealed class CrossVersionBatchTarget
{
    public string Name { get; init; } = string.Empty;

    public uint SourceOffset { get; init; }
}

public sealed class CrossVersionEvidence
{
    public CrossVersionEvidenceKind Kind { get; init; }

    public string KindText => Kind switch
    {
        CrossVersionEvidenceKind.BytePattern => "字节特征",
        CrossVersionEvidenceKind.FunctionBoundary => "函数边界",
        CrossVersionEvidenceKind.ExportSymbol => "导出符号",
        CrossVersionEvidenceKind.CrossReference => "交叉引用",
        CrossVersionEvidenceKind.StringReference => "字符串",
        CrossVersionEvidenceKind.ImportReference => "导入符号",
        CrossVersionEvidenceKind.LiveMemory => "实时内存",
        _ => string.Empty
    };

    public string LocatorName { get; init; } = string.Empty;

    public string SourceAnchor { get; init; } = string.Empty;

    public string TargetMatch { get; init; } = string.Empty;

    public string Resolution { get; init; } = string.Empty;

    public double Score { get; init; }

    public string ScoreText => $"{Score:P0}";

    public uint? ResolvedRva { get; init; }

    public string ResolvedRvaText => ResolvedRva is { } rva
        ? $"0x{rva:X}"
        : "—";

    public string ResolverText => Kind switch
    {
        CrossVersionEvidenceKind.BytePattern => "字节特征匹配",
        CrossVersionEvidenceKind.FunctionBoundary => "函数边界匹配",
        CrossVersionEvidenceKind.ExportSymbol => "导出符号匹配",
        CrossVersionEvidenceKind.CrossReference => "交叉引用解析",
        CrossVersionEvidenceKind.StringReference => "字符串匹配",
        CrossVersionEvidenceKind.ImportReference => "导入符号匹配",
        CrossVersionEvidenceKind.LiveMemory => "实时内存校验",
        _ => "未知方式"
    };

    public string ConfidenceText => Score switch
    {
        >= 0.8 => "高",
        >= 0.55 => "中",
        _ => "低"
    };

    public string Description { get; init; } = string.Empty;
}

public sealed class CrossVersionCandidate
{
    public uint TargetOffset { get; init; }

    public string TargetOffsetText => $"0x{TargetOffset:X}";

    public double Confidence { get; init; }

    public string ConfidenceText => $"{Confidence:P0}";

    public int EvidenceCount { get; init; }

    public string Explanation { get; init; } = string.Empty;

    public string DisplayName =>
        $"{TargetOffsetText}  ·  分数 {Confidence:F2}  ·  {EvidenceCount} 条证据";
}

public sealed class CrossVersionResult
{
    public string Name { get; init; } = string.Empty;

    public uint SourceOffset { get; init; }

    public string SourceOffsetText => $"0x{SourceOffset:X}";

    public CrossVersionResolutionState State { get; init; }

    public string StateText => State switch
    {
        CrossVersionResolutionState.Resolved => "已定位",
        CrossVersionResolutionState.Ambiguous => "存在歧义",
        CrossVersionResolutionState.Unresolved => "未找到",
        CrossVersionResolutionState.Failed => "分析失败",
        _ => string.Empty
    };

    public uint? ResolvedOffset { get; init; }

    public string ResolvedOffsetText => ResolvedOffset is { } offset
        ? $"0x{offset:X}"
        : string.Empty;

    public double Confidence { get; init; }

    public string ConfidenceText => $"{Confidence:P0}";

    public string ConfidenceLevelText => Confidence <= 0
        ? "—"
        : Confidence switch
        {
            >= 0.8 => "高",
            >= 0.55 => "中",
            _ => "低"
        };

    public int EvidenceCount => Evidence.Count;

    public string Explanation { get; init; } = string.Empty;

    public IReadOnlyList<CrossVersionEvidence> Evidence { get; init; } = [];

    public IReadOnlyList<CrossVersionCandidate> Candidates { get; init; } = [];
}

public sealed class CrossVersionProgress
{
    public int Completed { get; init; }

    public int Total { get; init; }

    public string Message { get; init; } = string.Empty;

    public double Percentage => Total <= 0
        ? 0
        : Completed * 100d / Total;
}
