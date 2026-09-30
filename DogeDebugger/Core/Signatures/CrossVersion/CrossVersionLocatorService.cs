using System.Collections.Concurrent;
using System.Text;
using DogeDebugger.Core.Disassembly;
using DogeDebugger.Core.Modules;

namespace DogeDebugger.Core.Signatures.CrossVersion;

public sealed class CrossVersionLocatorService
{
    private const int MaximumPatternLength = 128;
    private const int MaximumMatchesPerLocator = 64;
    private readonly DisassemblerService _disassembler = new();
    private readonly ConcurrentDictionary<LoadedPeImage, ReferenceIndex> _referenceIndexes = [];

    public Task<IReadOnlyList<CrossVersionResult>> LocateAsync(
        LoadedPeImage source,
        LoadedPeImage target,
        IReadOnlyList<CrossVersionBatchTarget> targets,
        CrossVersionTargetKind targetKind,
        IProgress<CrossVersionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(targets);
        return Task.Run(
            () => Locate(
                source,
                target,
                targets,
                targetKind,
                progress,
                cancellationToken),
            cancellationToken);
    }

    internal ReferenceIndex GetReferenceIndex(LoadedPeImage image) =>
        _referenceIndexes.GetOrAdd(image, BuildReferenceIndex);

    private IReadOnlyList<CrossVersionResult> Locate(
        LoadedPeImage source,
        LoadedPeImage target,
        IReadOnlyList<CrossVersionBatchTarget> targets,
        CrossVersionTargetKind requestedKind,
        IProgress<CrossVersionProgress>? progress,
        CancellationToken cancellationToken)
    {
        List<CrossVersionResult> results = new(targets.Count);
        for (int index = 0; index < targets.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CrossVersionBatchTarget targetItem = targets[index];
            progress?.Report(new CrossVersionProgress
            {
                Completed = index,
                Total = targets.Count,
                Message = $"正在定位 {targetItem.Name}..."
            });
            results.Add(LocateOne(
                source,
                target,
                targetItem,
                requestedKind,
                cancellationToken));
        }

        progress?.Report(new CrossVersionProgress
        {
            Completed = targets.Count,
            Total = targets.Count,
            Message = "批量定位完成。"
        });
        return results;
    }

    private CrossVersionResult LocateOne(
        LoadedPeImage source,
        LoadedPeImage targetImage,
        CrossVersionBatchTarget targetItem,
        CrossVersionTargetKind requestedKind,
        CancellationToken cancellationToken)
    {
        try
        {
            CrossVersionTargetKind kind = ResolveTargetKind(
                source,
                targetItem.SourceOffset,
                requestedKind,
                out uint anchorRva,
                out int relativeOffset);
            PatternData pattern = BuildPattern(
                source,
                anchorRva,
                kind,
                cancellationToken);
            if (!pattern.HasEnoughFixedBytes)
            {
                return new CrossVersionResult
                {
                    Name = targetItem.Name,
                    SourceOffset = targetItem.SourceOffset,
                    State = CrossVersionResolutionState.Unresolved,
                    Explanation = "源地址附近没有足够稳定的字节锚点。"
                };
            }

            Dictionary<uint, CandidateAccumulator> candidates = [];
            List<CrossVersionEvidence> evidence = [];
            AddBytePatternCandidates(
                source,
                targetImage,
                kind,
                pattern,
                relativeOffset,
                candidates,
                evidence,
                cancellationToken);
            AddExportCandidate(
                source,
                targetImage,
                anchorRva,
                candidates,
                evidence);
            AddCrossReferenceEvidence(
                source,
                targetImage,
                anchorRva,
                candidates,
                evidence,
                cancellationToken);
            AddStringEvidence(
                source,
                targetImage,
                targetItem.SourceOffset,
                kind,
                candidates,
                evidence,
                cancellationToken);

            if (candidates.Count == 0)
            {
                return new CrossVersionResult
                {
                    Name = targetItem.Name,
                    SourceOffset = targetItem.SourceOffset,
                    State = CrossVersionResolutionState.Unresolved,
                    Explanation = "所有独立定位器均未找到可靠候选。",
                    Evidence = evidence
                };
            }

            CrossVersionCandidate[] ranked = candidates
                .Select(pair =>
                {
                    double confidence = Math.Clamp(
                        pair.Value.Score +
                        (candidates.Count == 1 ? 0.15 : 0),
                        0.05,
                        0.99);
                    return new CrossVersionCandidate
                    {
                        TargetOffset = pair.Key,
                        Confidence = confidence,
                        EvidenceCount = pair.Value.EvidenceCount,
                        Explanation = pair.Value.Explanation
                    };
                })
                .OrderByDescending(candidate => candidate.Confidence)
                .ThenBy(candidate => candidate.TargetOffset)
                .ToArray();
            CrossVersionCandidate best = ranked[0];
            bool resolved = ranked.Length == 1 ||
                            (best.Confidence >= 0.82 &&
                             (ranked.Length == 1 ||
                              best.Confidence - ranked[1].Confidence >= 0.12));
            CrossVersionResolutionState state = resolved
                ? CrossVersionResolutionState.Resolved
                : CrossVersionResolutionState.Ambiguous;
            string explanation = resolved
                ? $"多个独立证据支持 B 版偏移 0x{best.TargetOffset:X}。"
                : $"找到 {ranked.Length} 个候选，置信度接近，需要人工确认。";
            return new CrossVersionResult
            {
                Name = targetItem.Name,
                SourceOffset = targetItem.SourceOffset,
                State = state,
                ResolvedOffset = resolved ? best.TargetOffset : null,
                Confidence = best.Confidence,
                Explanation = explanation,
                Evidence = evidence,
                Candidates = ranked
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new CrossVersionResult
            {
                Name = targetItem.Name,
                SourceOffset = targetItem.SourceOffset,
                State = CrossVersionResolutionState.Failed,
                Explanation = exception.Message
            };
        }
    }

    private CrossVersionTargetKind ResolveTargetKind(
        LoadedPeImage source,
        uint rva,
        CrossVersionTargetKind requestedKind,
        out uint anchorRva,
        out int relativeOffset)
    {
        anchorRva = rva;
        relativeOffset = 0;
        PeExport? export = source.FindExportByRva(rva);
        if (export is not null)
        {
            return CrossVersionTargetKind.Function;
        }

        if (requestedKind != CrossVersionTargetKind.Auto)
        {
            return requestedKind;
        }

        PeSection? section = source.FindSection(rva);
        if (section?.IsExecutable == true)
        {
            return LooksLikeFunctionEntry(source, rva)
                ? CrossVersionTargetKind.Function
                : CrossVersionTargetKind.FunctionInterior;
        }

        if (section?.IsWritable == true)
        {
            return CrossVersionTargetKind.GlobalData;
        }

        return LooksLikeString(source, rva)
            ? CrossVersionTargetKind.String
            : CrossVersionTargetKind.Unknown;
    }

    private static bool LooksLikeFunctionEntry(LoadedPeImage image, uint rva)
    {
        byte[] bytes = image.ReadBytes(rva, 8);
        if (bytes.Length < 2)
        {
            return false;
        }

        return (bytes[0] == 0x55) ||
               (bytes[0] == 0x40 && (bytes[1] & 0xF0) == 0x50) ||
               (bytes[0] == 0x48 && bytes.Length >= 4 &&
                (bytes[3] == 0x24 || bytes[2] == 0xEC)) ||
               (bytes[0] == 0xE9) ||
               (bytes[0] == 0xEB);
    }

    private static bool LooksLikeString(LoadedPeImage image, uint rva)
    {
        byte[] ascii = image.ReadBytes(rva, 32);
        if (ascii.Length >= 4)
        {
            int printable = ascii.Count(value =>
                value is >= 0x20 and < 0x7F or 0 or 9 or 10 or 13);
            if (printable * 100 / ascii.Length >= 75 &&
                ascii.Any(value => value is >= 0x20 and < 0x7F))
            {
                return true;
            }
        }

        byte[] unicode = image.ReadBytes(rva, 64);
        if (unicode.Length >= 8)
        {
            int printable = 0;
            int characters = 0;
            for (int index = 0; index + 1 < unicode.Length; index += 2)
            {
                ushort character = BitConverter.ToUInt16(unicode, index);
                characters++;
                if (character == 0 ||
                    character is >= 0x20 and < 0x7F ||
                    character > 0x7F)
                {
                    printable++;
                }
            }

            if (characters > 0 && printable * 100 / characters >= 75)
            {
                return true;
            }
        }

        return false;
    }

    private PatternData BuildPattern(
        LoadedPeImage image,
        uint rva,
        CrossVersionTargetKind kind,
        CancellationToken cancellationToken)
    {
        if (kind is CrossVersionTargetKind.Function or
            CrossVersionTargetKind.FunctionInterior)
        {
            byte[] bytes = ReadUntilSectionEnd(image, rva, 512);
            if (bytes.Length != 0)
            {
                IReadOnlyList<InstructionSnapshot> instructions =
                    _disassembler.Disassemble(
                        bytes,
                        image.ImageBase + rva,
                        64,
                        image.Is64Bit);
                if (instructions.Count > 0)
                {
                    PatternData instructionPattern =
                        BuildInstructionPattern(instructions);
                    if (instructionPattern.HasEnoughFixedBytes)
                    {
                        return instructionPattern;
                    }
                }
            }
        }

        if (kind == CrossVersionTargetKind.String)
        {
            PatternData? stringPattern = BuildStringPattern(image, rva);
            if (stringPattern is { HasEnoughFixedBytes: true })
            {
                return stringPattern;
            }
        }

        byte[] raw = ReadUntilSectionEnd(image, rva, MaximumPatternLength);
        byte[] mask = Enumerable.Repeat((byte)1, raw.Length).ToArray();
        for (int index = 0; index + sizeof(ulong) <= raw.Length; index += sizeof(ulong))
        {
            ulong value = BitConverter.ToUInt64(raw, index);
            if (value >= image.ImageBase &&
                value < image.ImageBase + (ulong)image.ImageBytes.Length)
            {
                Array.Clear(mask, index, sizeof(ulong));
            }
        }

        return new PatternData(raw, mask, 0, string.Empty);
    }

    private static PatternData BuildInstructionPattern(
        IReadOnlyList<InstructionSnapshot> instructions)
    {
        List<byte> bytes = [];
        List<byte> masks = [];
        int fixedBytes = 0;
        foreach (InstructionSnapshot instruction in instructions)
        {
            if (instruction.Bytes.Length == 0)
            {
                break;
            }

            for (int index = 0; index < instruction.Bytes.Length; index++)
            {
                byte mask = index < instruction.FixedByteMask.Count
                    ? instruction.FixedByteMask[index]
                    : (byte)1;
                bytes.Add(instruction.Bytes[index]);
                masks.Add(mask);
                if (mask != 0)
                {
                    fixedBytes++;
                }
            }

            if (bytes.Count >= MaximumPatternLength || fixedBytes >= 32)
            {
                break;
            }
        }

        return new PatternData(
            bytes.ToArray(),
            masks.ToArray(),
            fixedBytes,
            string.Empty);
    }

    private static PatternData? BuildStringPattern(
        LoadedPeImage image,
        uint rva)
    {
        uint start = FindStringStart(image, rva);
        string ascii = image.ReadAscii(start, 256);
        if (ascii.Length >= 4)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(ascii);
            return new PatternData(
                bytes,
                Enumerable.Repeat((byte)1, bytes.Length).ToArray(),
                bytes.Length,
                string.Empty);
        }

        string unicode = image.ReadUnicode(start, 128);
        if (unicode.Length >= 4)
        {
            byte[] bytes = Encoding.Unicode.GetBytes(unicode);
            return new PatternData(
                bytes,
                Enumerable.Repeat((byte)1, bytes.Length).ToArray(),
                bytes.Length,
                string.Empty);
        }

        return null;
    }

    private static uint FindStringStart(LoadedPeImage image, uint rva)
    {
        PeSection? section = image.FindSection(rva);
        if (section is null)
        {
            return rva;
        }

        uint start = rva;
        uint minimum = section.VirtualAddress;
        while (start > minimum)
        {
            byte[] previous = image.ReadBytes(start - 1, 1);
            if (previous.Length == 0 ||
                previous[0] == 0 ||
                previous[0] is < 0x20 or >= 0x7F)
            {
                break;
            }

            start--;
        }

        return start;
    }

    private void AddBytePatternCandidates(
        LoadedPeImage source,
        LoadedPeImage target,
        CrossVersionTargetKind kind,
        PatternData pattern,
        int relativeOffset,
        Dictionary<uint, CandidateAccumulator> candidates,
        List<CrossVersionEvidence> evidence,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PatternMatch> matches = FindPattern(
            target,
            pattern,
            kind,
            cancellationToken);
        if (matches.Count == 0)
        {
            return;
        }

        foreach (PatternMatch match in matches)
        {
            long resolved = match.Rva + (long)relativeOffset;
            if (resolved < 0 || resolved > uint.MaxValue)
            {
                continue;
            }

            uint resolvedRva = (uint)resolved;
            double score = 0.55 +
                           (matches.Count == 1 ? 0.15 : 0) +
                           Math.Min(0.1, pattern.FixedByteCount / 320d);
            AddCandidate(
                candidates,
                resolvedRva,
                score,
                $"字节特征命中，固定字节 {pattern.FixedByteCount}。");
            evidence.Add(new CrossVersionEvidence
            {
                Kind = CrossVersionEvidenceKind.BytePattern,
                LocatorName = "字节特征",
                SourceAnchor = $"0x{source.ImageBase + pattern.SourceRva:X}",
                TargetMatch = $"0x{target.ImageBase + resolvedRva:X}",
                Resolution = matches.Count == 1 ? "唯一命中" : $"{matches.Count} 处命中",
                Score = score,
                ResolvedRva = resolvedRva,
                Description = $"目标{kind}区域生成的带通配字节特征。"
            });
        }
    }

    private void AddExportCandidate(
        LoadedPeImage source,
        LoadedPeImage target,
        uint sourceRva,
        Dictionary<uint, CandidateAccumulator> candidates,
        List<CrossVersionEvidence> evidence)
    {
        PeExport? sourceExport = source.FindExportByRva(sourceRva);
        if (sourceExport is null)
        {
            return;
        }

        PeExport? targetExport = target.FindExportByName(sourceExport.Name);
        if (targetExport is null || targetExport.ForwarderName is not null)
        {
            return;
        }

        AddCandidate(
            candidates,
            targetExport.FunctionRva,
            0.72,
            $"同名导出 {targetExport.Name}。");
        evidence.Add(new CrossVersionEvidence
        {
            Kind = CrossVersionEvidenceKind.ExportSymbol,
            LocatorName = "导出符号",
            SourceAnchor = sourceExport.Name,
            TargetMatch = $"0x{targetExport.FunctionRva:X}",
            Resolution = "同名导出",
            Score = 0.72,
            ResolvedRva = targetExport.FunctionRva,
            Description = "A/B 两版导出名称一致。"
        });
    }

    private void AddCrossReferenceEvidence(
        LoadedPeImage source,
        LoadedPeImage target,
        uint sourceRva,
        Dictionary<uint, CandidateAccumulator> candidates,
        List<CrossVersionEvidence> evidence,
        CancellationToken cancellationToken)
    {
        ReferenceIndex sourceIndex = GetReferenceIndex(source);
        if (!sourceIndex.References.TryGetValue(
                sourceRva,
                out List<CrossReferenceSite>? sourceReferences) ||
            sourceReferences.Count == 0)
        {
            return;
        }

        evidence.Add(new CrossVersionEvidence
        {
            Kind = CrossVersionEvidenceKind.CrossReference,
            LocatorName = "交叉引用",
            SourceAnchor = $"A 版引用 {sourceReferences.Count} 处",
            TargetMatch = string.Empty,
            Resolution = "用于候选共识",
            Score = 0.15,
            Description = string.Join(
                "；",
                sourceReferences.Take(3).Select(reference =>
                    $"0x{reference.SourceRva:X}: {reference.Text}"))
        });
        ReferenceIndex targetIndex = GetReferenceIndex(target);
        foreach ((uint candidateRva, CandidateAccumulator accumulator) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!targetIndex.References.TryGetValue(
                    candidateRva,
                    out List<CrossReferenceSite>? targetReferences))
            {
                continue;
            }

            int sourceCount = sourceReferences.Count;
            int targetCount = targetReferences.Count;
            double score = sourceCount == targetCount
                ? 0.18
                : 0.08;
            accumulator.Score += score;
            accumulator.EvidenceCount++;
            accumulator.Explanation +=
                $" B 版存在 {targetCount} 处交叉引用。";
        }
    }

    private void AddStringEvidence(
        LoadedPeImage source,
        LoadedPeImage target,
        uint sourceRva,
        CrossVersionTargetKind kind,
        Dictionary<uint, CandidateAccumulator> candidates,
        List<CrossVersionEvidence> evidence,
        CancellationToken cancellationToken)
    {
        if (kind != CrossVersionTargetKind.String && !LooksLikeString(source, sourceRva))
        {
            return;
        }

        PatternData? pattern = BuildStringPattern(source, sourceRva);
        if (pattern is null || !pattern.HasEnoughFixedBytes)
        {
            return;
        }

        IReadOnlyList<PatternMatch> matches = FindPattern(
            target,
            pattern,
            CrossVersionTargetKind.String,
            cancellationToken);
        foreach (PatternMatch match in matches)
        {
            AddCandidate(
                candidates,
                match.Rva,
                0.62,
                "字符串特征命中。");
        }

        if (matches.Count > 0)
        {
            evidence.Add(new CrossVersionEvidence
            {
                Kind = CrossVersionEvidenceKind.StringReference,
                LocatorName = "字符串",
                SourceAnchor = Encoding.ASCII.GetString(pattern.Bytes).TrimEnd('\0'),
                TargetMatch = string.Join(
                    ", ",
                    matches.Take(3).Select(match => $"0x{match.Rva:X}")),
                Resolution = matches.Count == 1 ? "唯一命中" : $"{matches.Count} 处命中",
                Score = 0.18,
                ResolvedRva = matches[0].Rva,
                Description = "使用 A 版字符串内容在 B 版中定位。"
            });
        }
    }

    private IReadOnlyList<PatternMatch> FindPattern(
        LoadedPeImage image,
        PatternData pattern,
        CrossVersionTargetKind kind,
        CancellationToken cancellationToken)
    {
        if (!pattern.HasEnoughFixedBytes || pattern.Bytes.Length == 0)
        {
            return [];
        }

        List<PatternMatch> matches = [];
        IEnumerable<PeSection> sections = kind is CrossVersionTargetKind.String
            ? image.Metadata.Sections
            : image.Metadata.Sections.Where(section => section.IsExecutable);
        foreach (PeSection section in sections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int sectionOffset = checked((int)section.VirtualAddress);
            int sectionLength = checked((int)Math.Min(
                Math.Max(section.VirtualSize, section.RawSize),
                (uint)Math.Max(0, image.ImageBytes.Length - sectionOffset)));
            if (sectionLength < pattern.Bytes.Length)
            {
                continue;
            }

            ReadOnlySpan<byte> bytes = image.ImageBytes.AsSpan(
                sectionOffset,
                sectionLength);
            int last = bytes.Length - pattern.Bytes.Length;
            for (int offset = 0; offset <= last; offset++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool match = true;
                for (int index = 0; index < pattern.Bytes.Length; index++)
                {
                    if (pattern.Mask[index] != 0 &&
                        bytes[offset + index] != pattern.Bytes[index])
                    {
                        match = false;
                        break;
                    }
                }

                if (!match)
                {
                    continue;
                }

                matches.Add(new PatternMatch(
                    section.VirtualAddress + (uint)offset));
                if (matches.Count >= MaximumMatchesPerLocator)
                {
                    return matches;
                }
            }
        }

        return matches;
    }

    private ReferenceIndex BuildReferenceIndex(LoadedPeImage image)
    {
        Dictionary<uint, List<CrossReferenceSite>> references = [];
        foreach (PeSection section in image.Metadata.Sections.Where(
                     section => section.IsExecutable))
        {
            int sectionOffset = checked((int)section.VirtualAddress);
            int sectionLength = checked((int)Math.Min(
                Math.Max(section.VirtualSize, section.RawSize),
                (uint)Math.Max(0, image.ImageBytes.Length - sectionOffset)));
            if (sectionLength <= 0)
            {
                continue;
            }

            IReadOnlyList<InstructionSnapshot> instructions =
                _disassembler.Disassemble(
                    image.ImageBytes.AsSpan(sectionOffset, sectionLength),
                    image.ImageBase + section.VirtualAddress,
                    1_000_000,
                    image.Is64Bit);
            foreach (InstructionSnapshot instruction in instructions)
            {
                foreach (ulong referencedAddress in instruction.ReferencedAddresses)
                {
                    if (referencedAddress < image.ImageBase ||
                        referencedAddress >=
                        image.ImageBase + (ulong)image.ImageBytes.Length)
                    {
                        continue;
                    }

                    uint referencedRva = checked(
                        (uint)(referencedAddress - image.ImageBase));
                    if (!references.TryGetValue(
                            referencedRva,
                            out List<CrossReferenceSite>? sites))
                    {
                        sites = [];
                        references[referencedRva] = sites;
                    }

                    sites.Add(new CrossReferenceSite
                    {
                        SourceRva = checked(
                            (uint)(instruction.Address - image.ImageBase)),
                        Text = instruction.Text
                    });
                }
            }
        }

        return new ReferenceIndex(references);
    }

    private static byte[] ReadUntilSectionEnd(
        LoadedPeImage image,
        uint rva,
        int maximumLength)
    {
        PeSection? section = image.FindSection(rva);
        int available = section is null
            ? maximumLength
            : checked((int)Math.Min(
                (uint)maximumLength,
                section.VirtualAddress +
                Math.Max(section.VirtualSize, section.RawSize) -
                rva));
        return available <= 0
            ? []
            : image.ReadBytes(rva, available);
    }

    private static void AddCandidate(
        Dictionary<uint, CandidateAccumulator> candidates,
        uint rva,
        double score,
        string explanation)
    {
        if (!candidates.TryGetValue(rva, out CandidateAccumulator? accumulator))
        {
            accumulator = new CandidateAccumulator();
            candidates[rva] = accumulator;
        }

        accumulator.Score += score;
        accumulator.EvidenceCount++;
        accumulator.Explanation = string.IsNullOrWhiteSpace(accumulator.Explanation)
            ? explanation.Trim()
            : $"{accumulator.Explanation} {explanation.Trim()}";
    }

    private sealed class CandidateAccumulator
    {
        public double Score { get; set; }

        public int EvidenceCount { get; set; }

        public string Explanation { get; set; } = string.Empty;
    }

    private sealed class PatternData
    {
        public PatternData(
            byte[] bytes,
            byte[] mask,
            int fixedByteCount,
            string description,
            uint sourceRva = 0)
        {
            Bytes = bytes;
            Mask = mask;
            FixedByteCount = fixedByteCount;
            Description = description;
            SourceRva = sourceRva;
        }

        public byte[] Bytes { get; }

        public byte[] Mask { get; }

        public int FixedByteCount { get; }

        public string Description { get; }

        public uint SourceRva { get; }

        public bool HasEnoughFixedBytes =>
            FixedByteCount >= 8 &&
            Bytes.Length >= 8;
    }

    private sealed record PatternMatch(uint Rva);

    internal sealed class CrossReferenceSite
    {
        public uint SourceRva { get; init; }

        public string Text { get; init; } = string.Empty;
    }

    internal sealed class ReferenceIndex
    {
        public ReferenceIndex(
            Dictionary<uint, List<CrossReferenceSite>> references)
        {
            References = references;
        }

        internal Dictionary<uint, List<CrossReferenceSite>> References { get; }

        public int Count(uint rva) =>
            References.TryGetValue(rva, out List<CrossReferenceSite>? sites)
                ? sites.Count
                : 0;

        public IReadOnlyList<string> Text(uint rva) =>
            References.TryGetValue(rva, out List<CrossReferenceSite>? sites)
                ? sites.Select(site => site.Text).ToArray()
                : [];
    }
}
