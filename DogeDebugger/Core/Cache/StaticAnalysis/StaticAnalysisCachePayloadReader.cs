using System.IO;
using System.Text;

namespace DogeDebugger.Core.Cache;

/// <summary>
/// Reads the complete static-analysis cache payload written by the original
/// analyzer. The reader consumes the stream exactly; any trailing or missing
/// bytes indicate a schema mismatch and are reported as an error.
/// </summary>
public static class StaticAnalysisCachePayloadReader
{
    private const int MaximumItemCount = 5_000_000;

    private const int MaximumShortString = 32768;

    private const int MaximumLongString = 4 * 1024 * 1024;

    public static StaticAnalysisCacheDocument Read(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            1024 * 1024,
            FileOptions.SequentialScan);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: false);
        StaticAnalysisCacheDocument document = Read(reader);
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException(
                $"Static-analysis cache has {stream.Length - stream.Position} " +
                "unexpected trailing bytes.");
        }

        return document;
    }

    public static StaticAnalysisCacheDocument Read(BinaryReader reader)
    {
        if (reader.ReadUInt32() != StaticAnalysisCacheHeader.Magic)
        {
            throw new InvalidDataException("Invalid static-analysis cache magic.");
        }

        ushort version = reader.ReadUInt16();
        if (version != StaticAnalysisCacheHeader.SchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported static-analysis cache version: {version}.");
        }

        string moduleName = ReadString(reader, MaximumShortString);
        string modulePath = ReadString(reader, MaximumLongString);
        ulong baseAddress = reader.ReadUInt64();
        uint imageSize = reader.ReadUInt32();
        uint entryPointRva = reader.ReadUInt32();
        StaticAnalysisMachine machine = (StaticAnalysisMachine)reader.ReadUInt32();
        string fileHash = ReadString(reader, 256);
        DateTime analysisTime = DateTime.FromBinary(reader.ReadInt64());
        TimeSpan duration = TimeSpan.FromTicks(reader.ReadInt64());

        IReadOnlyList<StaticAnalysisSection> sections = ReadSections(reader);
        StaticAnalysisFunctionAnalysis functions = ReadFunctionAnalysis(reader);
        StaticAnalysisCrossReferenceAnalysis crossReferences = ReadCrossReferenceAnalysis(reader);
        StaticAnalysisStringAnalysis strings = ReadStringAnalysis(reader);
        StaticAnalysisSemanticSummary? semantics = ReadSemanticSummary(reader);

        return new StaticAnalysisCacheDocument
        {
            Version = version,
            ModuleName = moduleName,
            ModulePath = modulePath,
            BaseAddress = baseAddress,
            ImageSize = imageSize,
            EntryPointRva = entryPointRva,
            Machine = machine,
            FileHash = fileHash,
            AnalysisTime = analysisTime,
            Duration = duration,
            Sections = sections,
            Functions = functions,
            CrossReferences = crossReferences,
            Strings = strings,
            Semantics = semantics
        };
    }

    private static IReadOnlyList<StaticAnalysisSection> ReadSections(BinaryReader reader)
    {
        int count = ReadCount(reader);
        List<StaticAnalysisSection> sections = new(count);
        for (int index = 0; index < count; index++)
        {
            sections.Add(new StaticAnalysisSection
            {
                Name = ReadString(reader, MaximumShortString),
                VirtualAddress = reader.ReadUInt32(),
                VirtualSize = reader.ReadUInt32(),
                RawOffset = reader.ReadUInt32(),
                RawSize = reader.ReadUInt32(),
                Characteristics = reader.ReadUInt32()
            });
        }

        return sections;
    }

    private static StaticAnalysisFunctionAnalysis ReadFunctionAnalysis(BinaryReader reader)
    {
        string name = ReadString(reader, MaximumLongString);
        StaticAnalysisMachine machine = (StaticAnalysisMachine)reader.ReadUInt32();
        ulong baseAddress = reader.ReadUInt64();
        uint imageSize = reader.ReadUInt32();
        uint entryPointRva = reader.ReadUInt32();
        uint[] counters = ReadUInt32Array(reader, 6);
        int functionCount = ReadCount(reader);
        List<StaticAnalysisFunction> functions = new(functionCount);
        for (int index = 0; index < functionCount; index++)
        {
            functions.Add(ReadFunction(reader));
        }

        int flowCount = ReadCount(reader);
        List<StaticAnalysisFlowRecord> flowRecords = new(flowCount);
        for (int index = 0; index < flowCount; index++)
        {
            flowRecords.Add(new StaticAnalysisFlowRecord
            {
                SourceRva = reader.ReadUInt32(),
                TargetRva = reader.ReadUInt32(),
                Data = reader.ReadUInt64(),
                KindValue = reader.ReadUInt32()
            });
        }

        return new StaticAnalysisFunctionAnalysis
        {
            Name = name,
            Machine = machine,
            BaseAddress = baseAddress,
            ImageSize = imageSize,
            EntryPointRva = entryPointRva,
            Counters = counters,
            Functions = functions,
            FlowRecords = flowRecords
        };
    }

    private static StaticAnalysisFunction ReadFunction(BinaryReader reader)
    {
        uint startRva = reader.ReadUInt32();
        uint relatedRva1 = reader.ReadUInt32();
        uint relatedRva2 = reader.ReadUInt32();
        uint minBlockRva = reader.ReadUInt32();
        uint maxBlockEndRva = reader.ReadUInt32();
        uint instructionCount = reader.ReadUInt32();
        uint flowEdgeCount = reader.ReadUInt32();
        uint basicBlockCount = reader.ReadUInt32();
        uint primaryMetric = reader.ReadUInt32();
        ulong flags = reader.ReadUInt64();
        ulong extendedFlags = reader.ReadUInt64();
        byte classification = reader.ReadByte();
        IReadOnlyList<uint> blockStartRvas = ReadUInt32List(reader);
        IReadOnlyList<uint> successorRvas = ReadUInt32List(reader);
        IReadOnlyList<uint> instructionRvas = ReadUInt32List(reader);
        IReadOnlyList<uint> incomingRvas = ReadUInt32List(reader);
        IReadOnlyList<string> nameHints = ReadStringList(reader);
        IReadOnlyList<StaticAnalysisRange> blockRanges = ReadRangeList(reader);
        IReadOnlyList<StaticAnalysisFlowEdge> flowEdges = ReadFlowEdges(reader);
        IReadOnlyList<StaticAnalysisFunctionDetail> details = ReadFunctionDetails(reader);

        return new StaticAnalysisFunction
        {
            StartRva = startRva,
            RelatedRva1 = relatedRva1,
            RelatedRva2 = relatedRva2,
            MinBlockRva = minBlockRva,
            MaxBlockEndRva = maxBlockEndRva,
            InstructionCount = instructionCount,
            FlowEdgeCount = flowEdgeCount,
            AnalysisCounter = basicBlockCount,
            PrimaryMetric = primaryMetric,
            Flags = flags,
            ExtendedFlags = extendedFlags,
            Classification = classification,
            BlockStartRvas = blockStartRvas,
            SuccessorRvas = successorRvas,
            InstructionRvas = instructionRvas,
            IncomingRvas = incomingRvas,
            NameHints = nameHints,
            BlockRanges = blockRanges,
            FlowEdges = flowEdges,
            Details = details
        };
    }

    private static IReadOnlyList<StaticAnalysisFlowEdge> ReadFlowEdges(BinaryReader reader)
    {
        int count = ReadCount(reader);
        List<StaticAnalysisFlowEdge> edges = new(count);
        for (int index = 0; index < count; index++)
        {
            edges.Add(new StaticAnalysisFlowEdge
            {
                SourceStartRva = reader.ReadUInt32(),
                SourceEndRva = reader.ReadUInt32(),
                KindValue = reader.ReadUInt32(),
                FlowKind = reader.ReadByte(),
                ConditionValues = ReadUInt32List(reader),
                TargetRva = reader.ReadUInt32()
            });
        }

        return edges;
    }

    private static IReadOnlyList<StaticAnalysisFunctionDetail> ReadFunctionDetails(
        BinaryReader reader)
    {
        int count = ReadCount(reader);
        List<StaticAnalysisFunctionDetail> details = new(count);
        for (int index = 0; index < count; index++)
        {
            details.Add(new StaticAnalysisFunctionDetail
            {
                Value1 = reader.ReadUInt32(),
                Value2 = reader.ReadUInt32(),
                Value3 = reader.ReadByte(),
                Text = ReadString(reader, MaximumLongString),
                Ranges = ReadRangeList(reader),
                Values = ReadUInt32List(reader)
            });
        }

        return details;
    }

    private static StaticAnalysisCrossReferenceAnalysis ReadCrossReferenceAnalysis(
        BinaryReader reader)
    {
        string name = ReadString(reader, MaximumLongString);
        StaticAnalysisMachine machine = (StaticAnalysisMachine)reader.ReadUInt32();
        ulong baseAddress = reader.ReadUInt64();
        uint imageSize = reader.ReadUInt32();
        uint entryPointRva = reader.ReadUInt32();
        uint[] counters = ReadUInt32Array(reader, 5);
        int count = ReadCount(reader);
        List<StaticAnalysisCrossReference> references = new(count);
        for (int index = 0; index < count; index++)
        {
            references.Add(new StaticAnalysisCrossReference
            {
                SourceRva = reader.ReadUInt32(),
                TargetRva = reader.ReadUInt32(),
                SourceFunctionRva = reader.ReadUInt32(),
                TargetFunctionRva = reader.ReadUInt32(),
                KindValue = reader.ReadByte(),
                FlowKind = reader.ReadByte(),
                Flags = reader.ReadByte(),
                IsDirect = reader.ReadBoolean(),
                IsCall = reader.ReadBoolean(),
                IsJump = reader.ReadBoolean(),
                IsData = reader.ReadBoolean(),
                SourceText = ReadString(reader, MaximumShortString),
                TargetText = ReadString(reader, MaximumShortString),
                Details = ReadString(reader, MaximumLongString)
            });
        }

        return new StaticAnalysisCrossReferenceAnalysis
        {
            Name = name,
            Machine = machine,
            BaseAddress = baseAddress,
            ImageSize = imageSize,
            EntryPointRva = entryPointRva,
            Counters = counters,
            CrossReferences = references
        };
    }

    private static StaticAnalysisStringAnalysis ReadStringAnalysis(BinaryReader reader)
    {
        string name = ReadString(reader, MaximumLongString);
        StaticAnalysisMachine machine = (StaticAnalysisMachine)reader.ReadUInt32();
        ulong baseAddress = reader.ReadUInt64();
        uint imageSize = reader.ReadUInt32();
        uint[] counters = ReadUInt32Array(reader, 7);
        int count = ReadCount(reader);
        List<StaticAnalysisStringEntry> strings = new(count);
        for (int index = 0; index < count; index++)
        {
            strings.Add(ReadStringEntry(reader));
        }

        return new StaticAnalysisStringAnalysis
        {
            Name = name,
            Machine = machine,
            BaseAddress = baseAddress,
            ImageSize = imageSize,
            Counters = counters,
            Strings = strings
        };
    }

    private static StaticAnalysisStringEntry ReadStringEntry(BinaryReader reader)
    {
        uint rva = reader.ReadUInt32();
        uint length = reader.ReadUInt32();
        uint containingFunctionRva = reader.ReadUInt32();
        byte kindValue = reader.ReadByte();
        uint flags = reader.ReadUInt32();
        bool isUnicode = reader.ReadBoolean();
        bool isReferenced = reader.ReadBoolean();
        bool isInDataSection = reader.ReadBoolean();
        bool isWide = reader.ReadBoolean();
        string value = ReadString(reader, MaximumShortString);
        string sourceText = ReadString(reader, MaximumShortString);
        string details = ReadString(reader, MaximumLongString);
        uint referenceCount = reader.ReadUInt32();
        uint byteLength = reader.ReadUInt32();
        uint characterCount = reader.ReadUInt32();
        uint flags2 = reader.ReadUInt32();
        IReadOnlyList<uint> referenceRvas = ReadUInt32List(reader);

        return new StaticAnalysisStringEntry
        {
            Rva = rva,
            Length = length,
            ContainingFunctionRva = containingFunctionRva,
            KindValue = kindValue,
            Flags = flags,
            IsUnicode = isUnicode,
            IsReferenced = isReferenced,
            IsInDataSection = isInDataSection,
            IsWide = isWide,
            Value = value,
            SourceText = sourceText,
            Details = details,
            ReferenceCount = referenceCount,
            ByteLength = byteLength,
            CharacterCount = characterCount,
            Flags2 = flags2,
            ReferenceRvas = referenceRvas
        };
    }

    private static StaticAnalysisSemanticSummary? ReadSemanticSummary(BinaryReader reader)
    {
        if (!reader.ReadBoolean())
        {
            return null;
        }

        string name = ReadString(reader, MaximumLongString);
        StaticAnalysisMachine machine = (StaticAnalysisMachine)reader.ReadUInt32();
        ulong baseAddress = reader.ReadUInt64();
        uint imageSize = reader.ReadUInt32();
        uint entryPointRva = reader.ReadUInt32();
        uint[] counters = ReadUInt32Array(reader, 17);

        int entryCount = ReadCount(reader);
        List<StaticAnalysisSemanticEntry> entries = new(entryCount);
        for (int index = 0; index < entryCount; index++)
        {
            entries.Add(new StaticAnalysisSemanticEntry
            {
                PrimaryText = ReadString(reader, MaximumShortString),
                SecondaryText = ReadString(reader, MaximumShortString),
                Details = ReadString(reader, MaximumLongString),
                Rva = reader.ReadUInt32()
            });
        }

        int typeCount = ReadCount(reader);
        List<StaticAnalysisSemanticType> types = new(typeCount);
        for (int index = 0; index < typeCount; index++)
        {
            types.Add(ReadSemanticType(reader));
        }

        return new StaticAnalysisSemanticSummary
        {
            Name = name,
            Machine = machine,
            BaseAddress = baseAddress,
            ImageSize = imageSize,
            EntryPointRva = entryPointRva,
            Counters = counters,
            Entries = entries,
            Types = types
        };
    }

    private static StaticAnalysisSemanticType ReadSemanticType(BinaryReader reader)
    {
        uint value1 = reader.ReadUInt32();
        uint value2 = reader.ReadUInt32();
        uint value3 = reader.ReadUInt32();
        uint value4 = reader.ReadUInt32();
        uint value5 = reader.ReadUInt32();
        uint value6 = reader.ReadUInt32();
        uint value7 = reader.ReadUInt32();
        string name = ReadString(reader, MaximumShortString);
        IReadOnlyList<StaticAnalysisRange> ranges = ReadRangeList(reader);
        IReadOnlyList<string> texts = ReadStringList(reader);
        uint[] counters = ReadUInt32Array(reader, 11);
        bool flag = reader.ReadBoolean();
        int memberCount = ReadCount(reader);
        List<StaticAnalysisSemanticMember> members = new(memberCount);
        for (int index = 0; index < memberCount; index++)
        {
            members.Add(new StaticAnalysisSemanticMember
            {
                Name = ReadString(reader, MaximumShortString),
                Value1 = reader.ReadUInt32(),
                Value2 = reader.ReadUInt32(),
                TypeName = ReadString(reader, MaximumLongString),
                DeclaringTypeName = ReadString(reader, MaximumShortString),
                Details = ReadString(reader, MaximumLongString)
            });
        }

        uint totalMemberCount = reader.ReadUInt32();
        return new StaticAnalysisSemanticType
        {
            Value1 = value1,
            Value2 = value2,
            Value3 = value3,
            Value4 = value4,
            Value5 = value5,
            Value6 = value6,
            Value7 = value7,
            Name = name,
            Ranges = ranges,
            Texts = texts,
            Counters = counters,
            Flag = flag,
            Members = members,
            MemberCount = totalMemberCount
        };
    }

    private static IReadOnlyList<StaticAnalysisRange> ReadRangeList(BinaryReader reader)
    {
        int count = ReadCount(reader);
        List<StaticAnalysisRange> ranges = new(count);
        for (int index = 0; index < count; index++)
        {
            ranges.Add(new StaticAnalysisRange
            {
                StartRva = reader.ReadUInt32(),
                EndRva = reader.ReadUInt32()
            });
        }

        return ranges;
    }

    private static IReadOnlyList<uint> ReadUInt32List(BinaryReader reader)
    {
        int count = ReadCount(reader);
        List<uint> values = new(count);
        for (int index = 0; index < count; index++)
        {
            values.Add(reader.ReadUInt32());
        }

        return values;
    }

    private static uint[] ReadUInt32Array(BinaryReader reader, int count)
    {
        uint[] values = new uint[count];
        for (int index = 0; index < count; index++)
        {
            values[index] = reader.ReadUInt32();
        }

        return values;
    }

    private static IReadOnlyList<string> ReadStringList(BinaryReader reader)
    {
        int count = ReadCount(reader);
        List<string> values = new(count);
        for (int index = 0; index < count; index++)
        {
            values.Add(ReadString(reader, MaximumLongString));
        }

        return values;
    }

    private static int ReadCount(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > MaximumItemCount)
        {
            throw new InvalidDataException(
                $"Serialized collection count {count} exceeds the supported limit.");
        }

        return count;
    }

    private static string ReadString(BinaryReader reader, int maximumLength)
    {
        int length = Read7BitEncodedInt(reader);
        if (length < 0 || length > maximumLength)
        {
            throw new InvalidDataException(
                $"Serialized string length {length} exceeds the supported limit.");
        }

        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException(
                "Unexpected end of file while reading a serialized string.");
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static int Read7BitEncodedInt(BinaryReader reader)
    {
        uint result = 0;
        int shift = 0;
        while (true)
        {
            byte value = reader.ReadByte();
            result |= (uint)(value & 0x7F) << shift;
            if ((value & 0x80) == 0)
            {
                return (int)result;
            }

            shift += 7;
            if (shift >= 35)
            {
                throw new InvalidDataException("Invalid 7-bit encoded integer.");
            }
        }
    }
}
