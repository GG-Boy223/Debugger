using DogeDebugger.Core.Process;

namespace DogeDebugger.Core.Signatures.CrossVersion;

/// <summary>
/// Verifies a cross-version result against live target memory. The source
/// bytes come from the A-version image and the target bytes are read from the
/// attached process at the resolved module address.
/// </summary>
public static class LiveMemoryVerifier
{
    public const int DefaultSampleLength = 32;

    private const double MatchThreshold = 0.75;

    private const double MismatchThreshold = 0.5;

    public static CrossVersionResult Verify(
        CrossVersionResult result,
        LoadedPeImage? sourceImage,
        ITargetProcess target,
        ulong targetModuleBase,
        int sampleLength = DefaultSampleLength)
    {
        if (sourceImage is null || !target.IsOpen || sampleLength <= 0)
        {
            return result;
        }

        uint? candidateOffset = result.ResolvedOffset ??
            result.Candidates.FirstOrDefault()?.TargetOffset;
        if (candidateOffset is null)
        {
            return result;
        }

        byte[] expected = sourceImage.ReadBytes(result.SourceOffset, sampleLength);
        if (expected.Length == 0)
        {
            return result;
        }

        ulong address = targetModuleBase + candidateOffset.Value;
        byte[] actual = target.ReadBytes(address, expected.Length);
        int compared = Math.Min(expected.Length, actual.Length);
        int equal = 0;
        for (int index = 0; index < compared; index++)
        {
            if (expected[index] == actual[index])
            {
                equal++;
            }
        }

        bool readSucceeded = compared == expected.Length;
        double ratio = compared == 0 ? 0 : equal / (double)compared;
        CrossVersionEvidence evidence = new()
        {
            Kind = CrossVersionEvidenceKind.LiveMemory,
            LocatorName = "实时内存",
            SourceAnchor = $"0x{result.SourceOffset:X}",
            TargetMatch = $"0x{address:X}",
            Resolution = !readSucceeded
                ? "读取失败"
                : ratio >= MatchThreshold
                    ? "实时内存一致"
                    : ratio < MismatchThreshold
                        ? "实时内存不一致"
                        : "实时内存部分一致",
            Score = readSucceeded ? 0.6 + 0.38 * ratio : 0.2,
            ResolvedRva = candidateOffset,
            Description = readSucceeded
                ? $"实时内存校验一致率 {ratio:P0}（{equal}/{compared} 字节）。"
                : $"实时内存只读取到 {compared}/{expected.Length} 字节。"
        };
        IReadOnlyList<CrossVersionEvidence> evidenceList =
            [.. result.Evidence, evidence];

        if (readSucceeded && ratio >= MatchThreshold)
        {
            return new CrossVersionResult
            {
                Name = result.Name,
                SourceOffset = result.SourceOffset,
                State = CrossVersionResolutionState.Resolved,
                ResolvedOffset = candidateOffset,
                Confidence = Math.Max(result.Confidence, 0.6 + 0.38 * ratio),
                Explanation =
                    $"实时内存校验确认 B 版偏移 0x{candidateOffset.Value:X}。",
                Evidence = evidenceList,
                Candidates = result.Candidates
            };
        }

        if (readSucceeded && ratio < MismatchThreshold)
        {
            return new CrossVersionResult
            {
                Name = result.Name,
                SourceOffset = result.SourceOffset,
                State = CrossVersionResolutionState.Ambiguous,
                ResolvedOffset = null,
                Confidence = ratio,
                Explanation = "实时内存校验不一致，需要人工确认。",
                Evidence = evidenceList,
                Candidates = result.Candidates
            };
        }

        return new CrossVersionResult
        {
            Name = result.Name,
            SourceOffset = result.SourceOffset,
            State = result.State,
            ResolvedOffset = result.ResolvedOffset,
            Confidence = result.Confidence,
            Explanation = result.Explanation,
            Evidence = evidenceList,
            Candidates = result.Candidates
        };
    }
}
