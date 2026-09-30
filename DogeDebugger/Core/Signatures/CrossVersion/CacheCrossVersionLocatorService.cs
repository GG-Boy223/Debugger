using DogeDebugger.Core.Cache;
using DogeDebugger.Core.Disassembly;

namespace DogeDebugger.Core.Signatures.CrossVersion;

/// <summary>
/// Locates batch targets directly from parsed static-analysis cache documents.
/// This path does not require the original module image, so dump-only cache
/// pairs can participate in cross-version locating.
/// </summary>
public sealed class CacheCrossVersionLocatorService
{
    private const int MaximumCandidates = 8;

    private readonly InstructionAobLocator _aobLocator = new();

    private readonly DisassemblerService _disassembler = new();

    public IReadOnlyList<CrossVersionResult> Locate(
        StaticAnalysisCacheDocument source,
        StaticAnalysisCacheDocument target,
        IReadOnlyList<CrossVersionBatchTarget> targets,
        CrossVersionTargetKind kind,
        IProgress<CrossVersionProgress>? progress,
        CancellationToken cancellationToken,
        LoadedPeImage? sourceImage = null,
        LoadedPeImage? targetImage = null)
    {
        List<CrossVersionResult> results = new(targets.Count);
        if (source.Machine != target.Machine)
        {
            foreach (CrossVersionBatchTarget batchTarget in targets)
            {
                results.Add(FailedResult(
                    batchTarget,
                    "A/B 两版的机器类型不一致，无法比较静态分析缓存。"));
            }

            return results;
        }

        TargetIndex sourceIndex = new(source);
        TargetIndex targetIndex = new(target);
        for (int index = 0; index < targets.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(LocateTarget(
                source,
                target,
                sourceIndex,
                targetIndex,
                targets[index],
                kind,
                sourceImage,
                targetImage,
                cancellationToken));
            progress?.Report(new CrossVersionProgress
            {
                Completed = index + 1,
                Total = targets.Count,
                Message = $"正在比较缓存特征 {index + 1}/{targets.Count}。"
            });
        }

        return results;
    }

    private CrossVersionResult LocateTarget(
        StaticAnalysisCacheDocument source,
        StaticAnalysisCacheDocument target,
        TargetIndex sourceIndex,
        TargetIndex targetIndex,
        CrossVersionBatchTarget batchTarget,
        CrossVersionTargetKind kind,
        LoadedPeImage? sourceImage,
        LoadedPeImage? targetImage,
        CancellationToken cancellationToken)
    {
        StaticAnalysisFunction? sourceFunction =
            sourceIndex.FindFunction(batchTarget.SourceOffset);
        uint targetOffsetInFunction =
            sourceFunction is null
                ? 0
                : batchTarget.SourceOffset - sourceFunction.StartRva;
        Dictionary<uint, CandidateAccumulator> candidates = [];
        List<CrossVersionEvidence> evidence = [];

        uint? entryPointCandidate = null;
        if (sourceFunction is not null)
        {
            AddFunctionFingerprintEvidence(
                sourceFunction,
                targetIndex,
                targetOffsetInFunction,
                candidates,
                evidence);
            entryPointCandidate = AddEntryPointEvidence(
                source,
                targetIndex,
                sourceFunction,
                batchTarget.SourceOffset,
                targetOffsetInFunction,
                candidates,
                evidence);
        }

        AddStringEvidence(
            source,
            targetIndex,
            batchTarget.SourceOffset,
            candidates,
            evidence,
            kind);
        if (sourceImage is not null && targetImage is not null)
        {
            AddByteVerificationEvidence(
                sourceImage,
                targetImage,
                batchTarget.SourceOffset,
                candidates,
                evidence);
            if (sourceFunction is not null)
            {
                AddInstructionAobEvidence(
                    sourceImage,
                    targetImage,
                    batchTarget.SourceOffset,
                    candidates,
                    evidence,
                    cancellationToken);
            }

            AddReferenceResolverEvidence(
                source,
                targetImage,
                sourceImage,
                batchTarget.SourceOffset,
                candidates,
                evidence,
                cancellationToken);
        }

        if (sourceFunction is not null)
        {
            AddCrossReferenceEvidence(
                sourceFunction,
                sourceIndex,
                targetIndex,
                targetOffsetInFunction,
                candidates,
                evidence);
        }

        CandidateAccumulator[] ordered = candidates.Values
            .OrderByDescending(static candidate => candidate.Score)
            .ThenByDescending(static candidate => candidate.EvidenceCount)
            .Take(MaximumCandidates)
            .ToArray();
        if (ordered.Length == 0)
        {
            return new CrossVersionResult
            {
                Name = batchTarget.Name,
                SourceOffset = batchTarget.SourceOffset,
                State = CrossVersionResolutionState.Unresolved,
                Explanation = sourceFunction is null
                    ? "A 版静态分析缓存中没有覆盖该偏移的函数或引用。"
                    : "A 版函数在 B 版缓存中没有找到可比较的特征。",
                Evidence = evidence
            };
        }

        CandidateAccumulator best = ordered[0];
        CandidateAccumulator? second = ordered.Length > 1 ? ordered[1] : null;
        bool ambiguous = second is not null &&
                         best.Score - second.Score < 0.06 &&
                         best.KindCount <= second.KindCount;
        bool hasIndependentEvidence = best.KindCount >= 2 || best.Score >= 0.92;
        CrossVersionResolutionState state =
            ambiguous || (best.Score >= 0.55 && !hasIndependentEvidence)
                ? CrossVersionResolutionState.Ambiguous
                : hasIndependentEvidence && best.Score >= 0.72
                    ? CrossVersionResolutionState.Resolved
                    : CrossVersionResolutionState.Unresolved;
        if (entryPointCandidate is { } entryCandidate &&
            candidates.TryGetValue(entryCandidate, out CandidateAccumulator? entryAccumulator))
        {
            best = entryAccumulator;
            state = CrossVersionResolutionState.Resolved;
        }

        if (state == CrossVersionResolutionState.Resolved)
        {
            ordered = ordered
                .OrderByDescending(candidate => ReferenceEquals(candidate, best))
                .ThenByDescending(static candidate => candidate.Score)
                .ToArray();
        }

        IReadOnlyList<CrossVersionCandidate> candidateModels = ordered
            .Select(static candidate => new CrossVersionCandidate
            {
                TargetOffset = candidate.TargetRva,
                Confidence = candidate.Score,
                EvidenceCount = candidate.EvidenceCount,
                Explanation = candidate.Explanation
            })
            .ToArray();
        string explanation = state switch
        {
            CrossVersionResolutionState.Resolved =>
                $"多个独立证据支持 B 版偏移 0x{best.TargetRva:X}。",
            CrossVersionResolutionState.Ambiguous =>
                "存在多个候选，需要人工确认。",
            _ => "证据不足或相互冲突。"
        };
        return new CrossVersionResult
        {
            Name = batchTarget.Name,
            SourceOffset = batchTarget.SourceOffset,
            State = state,
            ResolvedOffset = state == CrossVersionResolutionState.Resolved
                ? best.TargetRva
                : null,
            Confidence = best.Score,
            Explanation = explanation,
            Evidence = evidence,
            Candidates = candidateModels
        };
    }

    private static void AddFunctionFingerprintEvidence(
        StaticAnalysisFunction sourceFunction,
        TargetIndex targetIndex,
        uint targetOffsetInFunction,
        Dictionary<uint, CandidateAccumulator> candidates,
        List<CrossVersionEvidence> evidence)
    {
        if (sourceFunction.BlockRanges.Count == 0)
        {
            return;
        }

        List<(StaticAnalysisFunction Function, double Score)> matches = [];
        for (int delta = -2; delta <= 2; delta++)
        {
            int blockCount = sourceFunction.BlockRanges.Count + delta;
            if (blockCount < 1 ||
                !targetIndex.FunctionsByBlockCount.TryGetValue(
                    blockCount,
                    out List<StaticAnalysisFunction>? functions))
            {
                continue;
            }

            foreach (StaticAnalysisFunction targetFunction in functions)
            {
                double match = ComputeBlockFingerprintMatch(sourceFunction, targetFunction);
                if (match >= 0.55)
                {
                    matches.Add((targetFunction, match));
                }
            }
        }

        if (matches.Count == 0)
        {
            return;
        }

        matches.Sort(static (left, right) => right.Score.CompareTo(left.Score));
        double bestScore = matches[0].Score;
        foreach ((StaticAnalysisFunction function, double match) in matches)
        {
            if (match < bestScore - 0.08)
            {
                break;
            }

            uint candidateRva = function.StartRva + targetOffsetInFunction;
            AddCandidate(
                candidates,
                candidateRva,
                CrossVersionEvidenceKind.FunctionBoundary,
                match,
                1.0,
                $"函数指纹匹配：基本块 {function.BlockRanges.Count}，" +
                $"指令 {function.InstructionCount}。");
        }

        StaticAnalysisFunction bestFunction = matches[0].Function;
        evidence.Add(new CrossVersionEvidence
        {
            Kind = CrossVersionEvidenceKind.FunctionBoundary,
            LocatorName = "函数指纹",
            SourceAnchor =
                $"0x{sourceFunction.StartRva:X} + 0x{targetOffsetInFunction:X}",
            TargetMatch = $"0x{bestFunction.StartRva:X}",
            Resolution = matches.Count == 1 ? "唯一指纹" : $"{matches.Count} 个相似指纹",
            Score = bestScore,
            ResolvedRva = bestFunction.StartRva + targetOffsetInFunction,
            Description =
                $"A 版函数包含 {sourceFunction.BlockRanges.Count} 个基本块、" +
                $"{sourceFunction.InstructionCount} 条指令；匹配度 {bestScore:P0}。"
        });
    }

    private static uint? AddEntryPointEvidence(
        StaticAnalysisCacheDocument source,
        TargetIndex targetIndex,
        StaticAnalysisFunction sourceFunction,
        uint sourceTargetRva,
        uint targetOffsetInFunction,
        Dictionary<uint, CandidateAccumulator> candidates,
        List<CrossVersionEvidence> evidence)
    {
        if (sourceTargetRva != source.EntryPointRva ||
            source.EntryPointRva < sourceFunction.MinBlockRva ||
            source.EntryPointRva >= sourceFunction.MaxBlockEndRva ||
            targetIndex.FileEntryPointRva == 0 ||
            targetIndex.FindFunction(targetIndex.FileEntryPointRva) is null)
        {
            return null;
        }

        uint candidateRva = targetIndex.FileEntryPointRva + targetOffsetInFunction;
        AddCandidate(
            candidates,
            candidateRva,
            CrossVersionEvidenceKind.FunctionBoundary,
            0.95,
            1.2,
            "模块入口点匹配。");
        evidence.Add(new CrossVersionEvidence
        {
            Kind = CrossVersionEvidenceKind.FunctionBoundary,
            LocatorName = "入口点",
            SourceAnchor = $"0x{source.EntryPointRva:X}",
            TargetMatch = $"0x{targetIndex.FileEntryPointRva:X}",
            Resolution = "入口点对应",
            Score = 0.95,
            ResolvedRva = candidateRva,
            Description = "A/B 两版模块入口点提供强定位锚点。"
        });
        return candidateRva;
    }

    private static void AddCrossReferenceEvidence(
        StaticAnalysisFunction sourceFunction,
        TargetIndex sourceIndex,
        TargetIndex targetIndex,
        uint targetOffsetInFunction,
        Dictionary<uint, CandidateAccumulator> candidates,
        List<CrossVersionEvidence> evidence)
    {
        if (!sourceIndex.IncomingReferenceCounts.TryGetValue(
                sourceFunction.StartRva,
                out int sourceCount) ||
            sourceCount == 0)
        {
            return;
        }

        double bestCloseness = 0;
        StaticAnalysisFunction? bestFunction = null;
        int bestCount = 0;
        foreach (StaticAnalysisFunction function in targetIndex.Functions)
        {
            if (Math.Abs(function.BlockRanges.Count - sourceFunction.BlockRanges.Count) > 2)
            {
                continue;
            }

            if (!targetIndex.IncomingReferenceCounts.TryGetValue(
                    function.StartRva,
                    out int count) ||
                count == 0)
            {
                continue;
            }

            double closeness = 1d -
                Math.Abs(count - sourceCount) / (double)Math.Max(count, sourceCount);
            if (closeness > bestCloseness)
            {
                bestCloseness = closeness;
                bestFunction = function;
                bestCount = count;
            }
        }

        if (bestFunction is null || bestCloseness < 0.8)
        {
            return;
        }

        uint candidateRva = bestFunction.StartRva + targetOffsetInFunction;
        if (!candidates.ContainsKey(candidateRva))
        {
            return;
        }

        double score = 0.5 + 0.35 * bestCloseness;
        AddCandidate(
            candidates,
            candidateRva,
            CrossVersionEvidenceKind.CrossReference,
            score,
            0.55,
            $"交叉引用数量匹配：入边 {bestCount}。");
        evidence.Add(new CrossVersionEvidence
        {
            Kind = CrossVersionEvidenceKind.CrossReference,
            LocatorName = "交叉引用",
            SourceAnchor = $"A 版入边 {sourceCount} 处",
            TargetMatch = $"0x{bestFunction.StartRva:X}（{bestCount} 处）",
            Resolution = "入边数量接近",
            Score = score,
            ResolvedRva = candidateRva,
            Description = "使用函数入边数量作为独立交叉证据。"
        });
    }

    private static void AddStringEvidence(
        StaticAnalysisCacheDocument source,
        TargetIndex targetIndex,
        uint sourceRva,
        Dictionary<uint, CandidateAccumulator> candidates,
        List<CrossVersionEvidence> evidence,
        CrossVersionTargetKind kind)
    {
        if (kind is not (CrossVersionTargetKind.Auto or CrossVersionTargetKind.String))
        {
            return;
        }

        StaticAnalysisStringEntry? sourceString = FindString(
            source.Strings.Strings,
            sourceRva);
        if (sourceString is null || sourceString.Value.Length < 4)
        {
            return;
        }

        if (!targetIndex.StringsByValue.TryGetValue(
                sourceString.Value,
                out List<StaticAnalysisStringEntry>? matches) ||
            matches.Count == 0 ||
            matches.Count > 8)
        {
            return;
        }

        uint offsetInString = sourceRva - sourceString.Rva;
        double score = matches.Count == 1 ? 0.88 : 0.62;
        string targetMatches = string.Join(
            ", ",
            matches.Take(3).Select(static match => $"0x{match.Rva:X}"));
        foreach (StaticAnalysisStringEntry match in matches)
        {
            uint candidateRva = match.Rva + offsetInString;
            AddCandidate(
                candidates,
                candidateRva,
                CrossVersionEvidenceKind.StringReference,
                score,
                0.9,
                $"字符串 \"{sourceString.Value}\" 匹配。");
        }

        evidence.Add(new CrossVersionEvidence
        {
            Kind = CrossVersionEvidenceKind.StringReference,
            LocatorName = "字符串",
            SourceAnchor = sourceString.Value,
            TargetMatch = targetMatches,
            Resolution = matches.Count == 1 ? "唯一命中" : $"{matches.Count} 处命中",
            Score = score,
            ResolvedRva = matches[0].Rva + offsetInString,
            Description = "使用 A 版字符串内容在 B 版缓存中定位。"
        });
    }

    private static void AddByteVerificationEvidence(
        LoadedPeImage sourceImage,
        LoadedPeImage targetImage,
        uint sourceRva,
        Dictionary<uint, CandidateAccumulator> candidates,
        List<CrossVersionEvidence> evidence)
    {
        CandidateAccumulator[] ordered = candidates.Values
            .OrderByDescending(static candidate => candidate.Score)
            .Take(MaximumCandidates)
            .ToArray();
        byte[] sourceBytes = sourceImage.ReadBytes(sourceRva, 32);
        if (sourceBytes.Length == 0)
        {
            return;
        }

        double bestRatio = 0;
        uint bestRva = 0;
        uint? bestResolvedRva = null;
        foreach (CandidateAccumulator candidate in ordered)
        {
            byte[] targetBytes = targetImage.ReadBytes(candidate.TargetRva, sourceBytes.Length);
            if (targetBytes.Length == 0)
            {
                continue;
            }

            int equal = 0;
            for (int index = 0; index < sourceBytes.Length; index++)
            {
                if (sourceBytes[index] == targetBytes[index])
                {
                    equal++;
                }
            }

            double ratio = equal / (double)sourceBytes.Length;
            if (ratio >= 0.5)
            {
                AddCandidate(
                    candidates,
                    candidate.TargetRva,
                    CrossVersionEvidenceKind.BytePattern,
                    0.55 + 0.4 * ratio,
                    1.2,
                    $"字节校验一致率 {ratio:P0}。");
            }

            if (ratio > bestRatio)
            {
                bestRatio = ratio;
                bestRva = candidate.TargetRva;
                bestResolvedRva = candidate.TargetRva;
            }
        }

        if (bestResolvedRva is null)
        {
            return;
        }

        evidence.Add(new CrossVersionEvidence
        {
            Kind = CrossVersionEvidenceKind.BytePattern,
            LocatorName = "字节校验",
            SourceAnchor = $"0x{sourceRva:X}",
            TargetMatch = $"0x{bestRva:X}",
            Resolution = bestRatio >= 0.8 ? "字节高度一致" : "字节部分一致",
            Score = 0.55 + 0.4 * bestRatio,
            ResolvedRva = bestResolvedRva,
            Description = $"使用模块转储对候选进行字节校验，一致率 {bestRatio:P0}。"
        });
    }

    public static double ComputeBlockFingerprintMatch(
        StaticAnalysisFunction source,
        StaticAnalysisFunction target)
    {
        List<(uint Offset, uint Size)> sourceBlocks = source.BlockRanges
            .Select(block => (
                Offset: block.StartRva - source.StartRva,
                Size: block.EndRva > block.StartRva
                    ? block.EndRva - block.StartRva
                    : 0u))
            .OrderBy(static block => block.Offset)
            .ToList();
        List<(uint Offset, uint Size)> targetBlocks = target.BlockRanges
            .Select(block => (
                Offset: block.StartRva - target.StartRva,
                Size: block.EndRva > block.StartRva
                    ? block.EndRva - block.StartRva
                    : 0u))
            .OrderBy(static block => block.Offset)
            .ToList();
        if (sourceBlocks.Count == 0 || targetBlocks.Count == 0)
        {
            return 0;
        }

        List<uint> remainingSizes = targetBlocks
            .Select(static block => block.Size)
            .ToList();
        int matched = 0;
        foreach ((uint _, uint size) in sourceBlocks)
        {
            int matchIndex = remainingSizes.FindIndex(
                candidate => Math.Abs((long)candidate - size) <= 4);
            if (matchIndex >= 0)
            {
                remainingSizes.RemoveAt(matchIndex);
                matched++;
            }
        }

        double sizeScore = matched / (double)Math.Max(sourceBlocks.Count, targetBlocks.Count);
        double countScore = 1d -
            Math.Abs(sourceBlocks.Count - targetBlocks.Count) /
            (double)Math.Max(sourceBlocks.Count, targetBlocks.Count);
        double instructionScore = Math.Min(source.InstructionCount, target.InstructionCount) /
            (double)Math.Max(
                Math.Max(source.InstructionCount, target.InstructionCount),
                1);
        double offsetScore = ComputeOffsetAgreement(sourceBlocks, targetBlocks);
        double score = Math.Clamp(
            0.4 * sizeScore +
            0.25 * countScore +
            0.25 * instructionScore +
            0.1 * offsetScore,
            0,
            1);
        double specificity = sourceBlocks.Count switch
        {
            <= 1 => 0.8,
            2 => 0.92,
            _ => 1.0
        };
        return score * specificity;
    }

    private static double ComputeOffsetAgreement(
        IReadOnlyList<(uint Offset, uint Size)> sourceBlocks,
        IReadOnlyList<(uint Offset, uint Size)> targetBlocks)
    {
        int matched = 0;
        int targetIndex = 0;
        foreach ((uint offset, uint size) in sourceBlocks)
        {
            while (targetIndex < targetBlocks.Count &&
                   targetBlocks[targetIndex].Offset < offset)
            {
                targetIndex++;
            }

            if (targetIndex < targetBlocks.Count &&
                Math.Abs((long)targetBlocks[targetIndex].Offset - offset) <= 16 &&
                Math.Abs((long)targetBlocks[targetIndex].Size - size) <= 8)
            {
                matched++;
            }
        }

        return matched / (double)Math.Max(sourceBlocks.Count, targetBlocks.Count);
    }

    private static StaticAnalysisStringEntry? FindString(
        IReadOnlyList<StaticAnalysisStringEntry> strings,
        uint rva)
    {
        foreach (StaticAnalysisStringEntry entry in strings)
        {
            uint length = entry.Length > 0
                ? entry.Length
                : (uint)entry.Value.Length;
            if (rva >= entry.Rva && rva < entry.Rva + Math.Max(length, 1))
            {
                return entry;
            }
        }

        return null;
    }

    private void AddInstructionAobEvidence(
        LoadedPeImage sourceImage,
        LoadedPeImage targetImage,
        uint sourceRva,
        Dictionary<uint, CandidateAccumulator> candidates,
        List<CrossVersionEvidence> evidence,
        CancellationToken cancellationToken)
    {
        InstructionAobPattern? strict = _aobLocator.Build(
            sourceImage,
            sourceRva,
            maximumInstructions: 24,
            targetFixedBytes: 32);
        IReadOnlyList<uint> strictMatches = strict is null
            ? []
            : _aobLocator.FindMatches(targetImage, strict, cancellationToken);
        if (strict is not null && strictMatches.Count > 0)
        {
            AddAobResult(
                candidates,
                evidence,
                strict,
                strictMatches,
                isTolerant: false);
            return;
        }

        InstructionAobPattern? tolerant = _aobLocator.Build(
            sourceImage,
            sourceRva,
            maximumInstructions: 6,
            targetFixedBytes: 12);
        IReadOnlyList<uint> tolerantMatches = tolerant is null
            ? []
            : _aobLocator.FindMatches(targetImage, tolerant, cancellationToken);
        if (tolerant is not null && tolerantMatches.Count > 0)
        {
            AddAobResult(
                candidates,
                evidence,
                tolerant,
                tolerantMatches,
                isTolerant: true);
        }
    }

    private static void AddAobResult(
        Dictionary<uint, CandidateAccumulator> candidates,
        List<CrossVersionEvidence> evidence,
        InstructionAobPattern pattern,
        IReadOnlyList<uint> matches,
        bool isTolerant)
    {
        bool unique = matches.Count == 1;
        double score = isTolerant
            ? unique ? 0.86 : 0.62
            : unique ? 0.98 : 0.78;
        double weight = isTolerant
            ? unique ? 1.1 : 0.8
            : unique ? 1.5 : 1.0;
        string mode = isTolerant ? "容错 AOB" : "严格 AOB";
        foreach (uint match in matches.Take(MaximumCandidates))
        {
            AddCandidate(
                candidates,
                match,
                CrossVersionEvidenceKind.BytePattern,
                score,
                weight,
                $"{mode}命中 0x{match:X}。");
        }

        evidence.Add(new CrossVersionEvidence
        {
            Kind = CrossVersionEvidenceKind.BytePattern,
            LocatorName = "目标指令",
            SourceAnchor = Truncate(pattern.Text, 72),
            TargetMatch = string.Join(
                ", ",
                matches.Take(3).Select(static match => $"0x{match:X}")),
            Resolution = unique
                ? $"{mode}唯一命中"
                : $"{matches.Count} 处命中",
            Score = score,
            ResolvedRva = matches[0],
            Description =
                $"固定字节 {pattern.FixedByteCount} 个，" +
                $"{(isTolerant ? "容错" : "严格")}目标指令特征。"
        });
    }

    private static string Truncate(string value, int maximumLength)
    {
        return value.Length <= maximumLength
            ? value
            : value[..maximumLength] + "...";
    }

    private void AddReferenceResolverEvidence(
        StaticAnalysisCacheDocument source,
        LoadedPeImage targetImage,
        LoadedPeImage sourceImage,
        uint sourceRva,
        Dictionary<uint, CandidateAccumulator> candidates,
        List<CrossVersionEvidence> evidence,
        CancellationToken cancellationToken)
    {
        StaticAnalysisCrossReference[] references = source.CrossReferences.CrossReferences
            .Where(reference => reference.TargetRva == sourceRva)
            .Take(8)
            .ToArray();
        if (references.Length == 0)
        {
            return;
        }

        foreach (StaticAnalysisCrossReference reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InstructionSnapshot? sourceInstruction = _disassembler.DisassembleOne(
                sourceImage.ReadBytes(reference.SourceRva, 32),
                sourceImage.ImageBase + reference.SourceRva,
                sourceImage.Is64Bit);
            if (sourceInstruction is null)
            {
                continue;
            }

            bool isBranch = sourceInstruction.NearBranchTarget is > 0;
            bool isDataReference = sourceInstruction.ReferencedAddresses.Count > 0;
            bool isAbsoluteReference = sourceInstruction.ImmediateValues.Any(
                value =>
                    value >= sourceImage.ImageBase &&
                    value < sourceImage.ImageBase +
                    (ulong)sourceImage.ImageBytes.Length) ||
                sourceInstruction.AbsoluteMemoryAddresses.Any(
                    value =>
                        value >= sourceImage.ImageBase &&
                        value < sourceImage.ImageBase +
                        (ulong)sourceImage.ImageBytes.Length);
            if (!isBranch && !isDataReference && !isAbsoluteReference)
            {
                continue;
            }

            InstructionAobPattern? pattern =
                _aobLocator.Build(
                    sourceImage,
                    reference.SourceRva,
                    maximumInstructions: 10,
                    targetFixedBytes: 20) ??
                _aobLocator.Build(
                    sourceImage,
                    reference.SourceRva,
                    maximumInstructions: 12,
                    targetFixedBytes: 8) ??
                _aobLocator.Build(
                    sourceImage,
                    reference.SourceRva,
                    maximumInstructions: 6,
                    targetFixedBytes: 4);
            if (pattern is null)
            {
                continue;
            }

            IReadOnlyList<uint> matches = _aobLocator.FindMatches(
                targetImage,
                pattern,
                cancellationToken);
            if (matches.Count == 0)
            {
                continue;
            }

            string resolver = isBranch
                ? "相对分支目标"
                : isAbsoluteReference
                    ? "绝对地址操作数"
                    : "相对寻址操作数";
            string locatorName = isBranch
                ? "调用方"
                : isAbsoluteReference
                    ? "绝对引用"
                    : "数据交叉引用";
            List<(uint Match, uint ResolvedRva)> resolved = [];
            foreach (uint match in matches.Take(MaximumCandidates))
            {
                InstructionSnapshot? targetInstruction = _disassembler.DisassembleOne(
                    targetImage.ReadBytes(match, 32),
                    targetImage.ImageBase + match,
                    targetImage.Is64Bit);
                uint? resolvedRva = ResolveReferencedRva(
                    targetInstruction,
                    targetImage);
                if (resolvedRva is null)
                {
                    continue;
                }

                resolved.Add((match, resolvedRva.Value));
            }

            if (resolved.Count == 0)
            {
                continue;
            }

            bool unique = resolved
                .Select(static entry => entry.ResolvedRva)
                .Distinct()
                .Count() == 1;
            double score = unique ? 0.95 : 0.64;
            double weight = unique ? 1.5 : 0.8;
            foreach ((uint match, uint resolvedRva) in resolved)
            {
                AddCandidate(
                    candidates,
                    resolvedRva,
                    CrossVersionEvidenceKind.CrossReference,
                    score,
                    weight,
                    $"{resolver}解析到 0x{resolvedRva:X}。");
                evidence.Add(new CrossVersionEvidence
                {
                    Kind = CrossVersionEvidenceKind.CrossReference,
                    LocatorName = locatorName,
                    SourceAnchor = $"0x{reference.SourceRva:X}",
                    TargetMatch = $"0x{match:X} → 0x{resolvedRva:X}",
                    Resolution = resolver,
                    Score = score,
                    ResolvedRva = resolvedRva,
                    Description =
                        $"通过 A 版交叉引用 0x{reference.SourceRva:X} " +
                        $"解析 B 版{(isBranch ? "分支目标" : "引用地址")}。"
                });
            }
        }
    }

    private static uint? ResolveReferencedRva(
        InstructionSnapshot? instruction,
        LoadedPeImage image)
    {
        if (instruction is null)
        {
            return null;
        }

        ulong? referenced = instruction.NearBranchTarget is > 0
            ? instruction.NearBranchTarget
            : instruction.ReferencedAddresses.Count > 0
                ? instruction.ReferencedAddresses[0]
                : FindAbsoluteAddress(instruction, image);
        if (referenced is null ||
            referenced < image.ImageBase ||
            referenced >= image.ImageBase + (ulong)image.ImageBytes.Length)
        {
            return null;
        }

        return (uint)(referenced.Value - image.ImageBase);
    }

    private static ulong? FindAbsoluteAddress(
        InstructionSnapshot instruction,
        LoadedPeImage image)
    {
        foreach (ulong value in instruction.AbsoluteMemoryAddresses
                     .Concat(instruction.ImmediateValues))
        {
            if (value >= image.ImageBase &&
                value < image.ImageBase + (ulong)image.ImageBytes.Length)
            {
                return value;
            }
        }

        return null;
    }

    private static void AddCandidate(
        Dictionary<uint, CandidateAccumulator> candidates,
        uint targetRva,
        CrossVersionEvidenceKind kind,
        double score,
        double weight,
        string explanation)
    {
        if (!candidates.TryGetValue(targetRva, out CandidateAccumulator? accumulator))
        {
            accumulator = new CandidateAccumulator(targetRva);
            candidates.Add(targetRva, accumulator);
        }

        accumulator.Add(kind, score, weight, explanation);
    }

    private static CrossVersionResult FailedResult(
        CrossVersionBatchTarget target,
        string explanation)
    {
        return new CrossVersionResult
        {
            Name = target.Name,
            SourceOffset = target.SourceOffset,
            State = CrossVersionResolutionState.Failed,
            Explanation = explanation
        };
    }

    private sealed class CandidateAccumulator
    {
        private readonly Dictionary<
            CrossVersionEvidenceKind,
            (double WeightedScore, double Weight)> _kindScores = [];
        private readonly List<string> _explanations = [];
        private readonly HashSet<CrossVersionEvidenceKind> _kinds = [];

        public CandidateAccumulator(uint targetRva)
        {
            TargetRva = targetRva;
        }

        public uint TargetRva { get; }

        public int EvidenceCount { get; private set; }

        public int KindCount => _kinds.Count;

        public double Score
        {
            get
            {
                double weightedScore = 0;
                double weight = 0;
                foreach ((double kindWeightedScore, double kindWeight) in _kindScores.Values)
                {
                    weightedScore += kindWeightedScore;
                    weight += kindWeight;
                }

                return weight <= 0 ? 0 : weightedScore / weight;
            }
        }

        public string Explanation => string.Join(" ", _explanations);

        public void Add(
            CrossVersionEvidenceKind kind,
            double score,
            double weight,
            string explanation)
        {
            _kinds.Add(kind);
            if (!_kindScores.TryGetValue(
                    kind,
                    out (double WeightedScore, double Weight) existing) ||
                score > existing.WeightedScore / Math.Max(existing.Weight, double.Epsilon))
            {
                _kindScores[kind] = (score * weight, weight);
            }

            EvidenceCount++;
            if (_explanations.Count < 4)
            {
                _explanations.Add(explanation);
            }
        }
    }

    private sealed class TargetIndex
    {
        private readonly StaticAnalysisFunction[] _functions;

        public TargetIndex(StaticAnalysisCacheDocument document)
        {
            FileEntryPointRva = document.EntryPointRva;
            _functions = document.Functions.Functions
                .OrderBy(static function => function.StartRva)
                .ToArray();
            Functions = _functions;
            FunctionsByBlockCount = _functions
                .GroupBy(static function => function.BlockRanges.Count)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.ToList());
            IncomingReferenceCounts = BuildIncomingReferenceCounts(document);
            StringsByValue = document.Strings.Strings
                .Where(static entry => entry.Value.Length >= 4)
                .GroupBy(static entry => entry.Value, StringComparer.Ordinal)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.ToList(),
                    StringComparer.Ordinal);
        }

        public IReadOnlyList<StaticAnalysisFunction> Functions { get; }

        public IReadOnlyDictionary<int, List<StaticAnalysisFunction>>
            FunctionsByBlockCount { get; }

        public IReadOnlyDictionary<uint, int> IncomingReferenceCounts { get; }

        public IReadOnlyDictionary<string, List<StaticAnalysisStringEntry>>
            StringsByValue { get; }

        public uint FileEntryPointRva { get; }

        public StaticAnalysisFunction? FindFunction(uint rva)
        {
            int low = 0;
            int high = _functions.Length - 1;
            int candidate = -1;
            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);
                if (_functions[middle].StartRva <= rva)
                {
                    candidate = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            for (int index = candidate; index >= 0 && index >= candidate - 32; index--)
            {
                StaticAnalysisFunction function = _functions[index];
                if (rva < function.MinBlockRva || rva >= function.MaxBlockEndRva)
                {
                    continue;
                }

                if (function.BlockRanges.Count == 0)
                {
                    return function;
                }

                foreach (StaticAnalysisRange block in function.BlockRanges)
                {
                    if (rva >= block.StartRva && rva < block.EndRva)
                    {
                        return function;
                    }
                }
            }

            return null;
        }

        private static IReadOnlyDictionary<uint, int> BuildIncomingReferenceCounts(
            StaticAnalysisCacheDocument document)
        {
            Dictionary<uint, int> counts = [];
            StaticAnalysisFunction[] functions = document.Functions.Functions
                .OrderBy(static function => function.StartRva)
                .ToArray();
            foreach (StaticAnalysisCrossReference reference in
                     document.CrossReferences.CrossReferences)
            {
                uint functionRva = reference.TargetFunctionRva;
                if (functionRva == 0)
                {
                    functionRva = FindFunction(functions, reference.TargetRva)?.StartRva ?? 0;
                }

                if (functionRva != 0)
                {
                    counts[functionRva] = counts.GetValueOrDefault(functionRva) + 1;
                }
            }

            return counts;
        }

        private static StaticAnalysisFunction? FindFunction(
            IReadOnlyList<StaticAnalysisFunction> functions,
            uint rva)
        {
            for (int index = 0; index < functions.Count; index++)
            {
                StaticAnalysisFunction function = functions[index];
                if (function.StartRva > rva)
                {
                    break;
                }

                if (rva >= function.MinBlockRva && rva < function.MaxBlockEndRva)
                {
                    return function;
                }
            }

            return null;
        }
    }
}
