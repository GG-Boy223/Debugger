using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using DogeDebugger.Core.Process;

namespace DogeDebugger.Core.Rtti;

public sealed class RttiAutoGuesser
{
    private const int MinimumUsefulScore = 48;
    private const int MaximumStringLength = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public RttiClassDefinition Guess(
        ITargetProcess process,
        string name,
        ulong baseAddress,
        int length,
        string comment = "")
    {
        ArgumentNullException.ThrowIfNull(process);
        if (length is < 1 or > 65536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                "读取长度必须在 1 ~ 65536 字节之间。");
        }

        byte[] memory = new byte[length];
        if (!process.TryReadBytes(baseAddress, memory))
        {
            throw new InvalidOperationException(
                $"无法从 0x{baseAddress:X} 完整读取 {length} 字节。");
        }

        RttiClassDefinition definition = new()
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"Auto_{baseAddress:X}" : name,
            Comment = string.IsNullOrWhiteSpace(comment)
                ? $"由所选地址 0x{baseAddress:X} 智能解析生成"
                : comment,
            BaseAddress = baseAddress,
            IsExpanded = true
        };

        int pointerSize = process.Is64Bit ? 8 : 4;
        int offset = 0;
        while (offset < memory.Length)
        {
            GuessCandidate candidate = FindBestCandidate(
                process,
                memory,
                offset,
                baseAddress,
                pointerSize);
            if (candidate.Score >= MinimumUsefulScore)
            {
                candidate.Field.Offset = offset;
                definition.Fields.Add(candidate.Field);
                offset += Math.Max(candidate.Size, 1);
                continue;
            }

            int zeroCount = CountZeroBytes(memory, offset);
            if (zeroCount >= 2)
            {
                AddHexFields(
                    definition,
                    memory.AsSpan(offset, zeroCount),
                    offset,
                    isPadding: true);
                offset += zeroCount;
                continue;
            }

            definition.Fields.Add(CreateHexField(
                memory[offset],
                offset,
                size: 1,
                isPadding: false));
            offset++;
        }

        return definition;
    }

    private static GuessCandidate FindBestCandidate(
        ITargetProcess process,
        byte[] memory,
        int offset,
        ulong baseAddress,
        int pointerSize)
    {
        GuessCandidate best = GuessCandidate.Invalid;
        Consider(TryReadPointer(
            process,
            memory,
            offset,
            baseAddress,
            pointerSize), ref best);
        Consider(TryReadUtf16(memory, offset), ref best);
        Consider(TryReadUtf8(memory, offset), ref best);
        Consider(TryReadMatrix(memory, offset), ref best);
        Consider(TryReadVector(memory, offset, 4), ref best);
        Consider(TryReadVector(memory, offset, 3), ref best);
        Consider(TryReadVector(memory, offset, 2), ref best);
        Consider(TryReadDouble(memory, offset), ref best);
        Consider(TryReadFloat(memory, offset), ref best);
        Consider(TryReadInteger(memory, offset), ref best);
        Consider(TryReadBoolean(memory, offset), ref best);
        return best;
    }

    private static void Consider(
        GuessCandidate currentBest,
        ref GuessCandidate best)
    {
        if (currentBest.Score == 0)
        {
            return;
        }

        if (best.Score == 0 ||
            currentBest.Score > best.Score ||
            (currentBest.Score == best.Score && currentBest.Size > best.Size))
        {
            best = currentBest;
        }
    }

    private static GuessCandidate TryReadPointer(
        ITargetProcess process,
        byte[] memory,
        int offset,
        ulong baseAddress,
        int pointerSize)
    {
        if (offset % pointerSize != 0 || offset + pointerSize > memory.Length)
        {
            return GuessCandidate.Invalid;
        }

        ulong pointer = pointerSize == 8
            ? BinaryPrimitives.ReadUInt64LittleEndian(memory.AsSpan(offset, 8))
            : BinaryPrimitives.ReadUInt32LittleEndian(memory.AsSpan(offset, 4));
        ulong maximum = process.Is64Bit ? 0x0000FFFFFFFFFFFFUL : uint.MaxValue;
        if (pointer < 65536 || pointer > maximum)
        {
            return GuessCandidate.Invalid;
        }

        byte[] pointed = new byte[64];
        int read = process.ReadBytesPartial(pointer, pointed);
        if (read <= 0)
        {
            if (!process.TryReadBytes(pointer, pointed.AsSpan(0, 16)))
            {
                return GuessCandidate.Invalid;
            }

            read = 16;
        }

        RttiFieldDefinition field = new()
        {
            Name = $"ptr_{offset:X4}",
            Offset = offset,
            Type = RttiNodeType.Pointer,
            PointerSize = pointerSize,
            Comment = "可读指针"
        };
        int score = 86;
        if (TryDecodeInlineUtf16(pointed.AsSpan(0, read), out _, out _))
        {
            field.PointedType = RttiNodeType.Utf16Text;
            field.Comment = "可读指针，目标像 UTF16 文本";
            score = 96;
        }
        else if (TryDecodeInlineUtf8(pointed.AsSpan(0, read), out _, out _))
        {
            field.PointedType = RttiNodeType.Utf8Text;
            field.Comment = "可读指针，目标像 UTF8 文本";
            score = 95;
        }

        return new GuessCandidate(field, pointerSize, score);
    }

    private static GuessCandidate TryReadUtf16(byte[] memory, int offset)
    {
        if (offset % 2 != 0 ||
            !TryDecodeInlineUtf16(
                memory.AsSpan(offset, Math.Min(MaximumStringLength, memory.Length - offset)),
                out int length,
                out string text))
        {
            return GuessCandidate.Invalid;
        }

        return new GuessCandidate(
            new RttiFieldDefinition
            {
                Name = $"text_{offset:X4}",
                Offset = offset,
                Type = RttiNodeType.Utf16Text,
                Length = length,
                Comment = $"内联 UTF16 文本：{TrimPreview(text)}"
            },
            length,
            90);
    }

    private static GuessCandidate TryReadUtf8(byte[] memory, int offset)
    {
        if (!TryDecodeInlineUtf8(
                memory.AsSpan(offset, Math.Min(MaximumStringLength, memory.Length - offset)),
                out int length,
                out string text))
        {
            return GuessCandidate.Invalid;
        }

        return new GuessCandidate(
            new RttiFieldDefinition
            {
                Name = $"text_{offset:X4}",
                Offset = offset,
                Type = RttiNodeType.Utf8Text,
                Length = length,
                Comment = $"内联 UTF8 文本：{TrimPreview(text)}"
            },
            length,
            88);
    }

    private static GuessCandidate TryReadMatrix(byte[] memory, int offset)
    {
        if (offset % 4 != 0 || offset + 64 > memory.Length)
        {
            return GuessCandidate.Invalid;
        }

        float[] values = new float[16];
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = BinaryPrimitives.ReadSingleLittleEndian(
                memory.AsSpan(offset + index * 4, 4));
            if (!float.IsFinite(values[index]) || MathF.Abs(values[index]) > 1_000_000f)
            {
                return GuessCandidate.Invalid;
            }
        }

        bool identity = IsIdentityMatrix(values);
        bool transform = IsTransformMatrix(values);
        if (!identity && !transform)
        {
            return GuessCandidate.Invalid;
        }

        return new GuessCandidate(
            new RttiFieldDefinition
            {
                Name = $"matrix_{offset:X4}",
                Offset = offset,
                Type = RttiNodeType.Matrix4x4,
                Comment = identity ? "疑似单位/变换矩阵" : "疑似 4x4 变换矩阵"
            },
            64,
            identity ? 98 : 92);
    }

    private static GuessCandidate TryReadVector(
        byte[] memory,
        int offset,
        int count)
    {
        int size = count * 4;
        if (offset % 4 != 0 || offset + size > memory.Length)
        {
            return GuessCandidate.Invalid;
        }

        bool anyMeaningful = false;
        for (int index = 0; index < count; index++)
        {
            float value = BinaryPrimitives.ReadSingleLittleEndian(
                memory.AsSpan(offset + index * 4, 4));
            if (!float.IsFinite(value) || MathF.Abs(value) > 1_000_000f)
            {
                return GuessCandidate.Invalid;
            }

            anyMeaningful |= MathF.Abs(value) > 1e-5f;
        }

        if (!anyMeaningful)
        {
            return GuessCandidate.Invalid;
        }

        RttiNodeType type = count switch
        {
            2 => RttiNodeType.Vector2,
            3 => RttiNodeType.Vector3,
            _ => RttiNodeType.Vector4
        };
        return new GuessCandidate(
            new RttiFieldDefinition
            {
                Name = $"vec{count}_{offset:X4}",
                Offset = offset,
                Type = type,
                Comment = $"连续 {count} 个合理浮点数"
            },
            size,
            count switch
            {
                2 => 74,
                3 => 78,
                _ => 80
            });
    }

    private static GuessCandidate TryReadDouble(byte[] memory, int offset)
    {
        if (offset % 8 != 0 || offset + 8 > memory.Length)
        {
            return GuessCandidate.Invalid;
        }

        double value = BinaryPrimitives.ReadDoubleLittleEndian(memory.AsSpan(offset, 8));
        double absolute = Math.Abs(value);
        if (!double.IsFinite(value) || absolute < 1e-6 || absolute > 1e12)
        {
            return GuessCandidate.Invalid;
        }

        return new GuessCandidate(
            new RttiFieldDefinition
            {
                Name = $"dbl_{offset:X4}",
                Offset = offset,
                Type = RttiNodeType.Double,
                Comment = "疑似双精度浮点数"
            },
            8,
            HasFraction(value) ? 62 : 55);
    }

    private static GuessCandidate TryReadFloat(byte[] memory, int offset)
    {
        if (offset % 4 != 0 || offset + 4 > memory.Length)
        {
            return GuessCandidate.Invalid;
        }

        float value = BinaryPrimitives.ReadSingleLittleEndian(memory.AsSpan(offset, 4));
        float absolute = MathF.Abs(value);
        if (!float.IsFinite(value) || absolute < 1e-5f || absolute > 1_000_000f)
        {
            return GuessCandidate.Invalid;
        }

        return new GuessCandidate(
            new RttiFieldDefinition
            {
                Name = $"flt_{offset:X4}",
                Offset = offset,
                Type = RttiNodeType.Float,
                Comment = "疑似浮点数"
            },
            4,
            HasFraction(value) ? 64 : 58);
    }

    private static GuessCandidate TryReadInteger(byte[] memory, int offset)
    {
        if (offset % 8 == 0 && offset + 8 <= memory.Length)
        {
            ulong unsignedValue = BinaryPrimitives.ReadUInt64LittleEndian(
                memory.AsSpan(offset, 8));
            long signedValue = BinaryPrimitives.ReadInt64LittleEndian(
                memory.AsSpan(offset, 8));
            if (BinaryPrimitives.ReadUInt32LittleEndian(memory.AsSpan(offset + 4, 4)) != 0 &&
                unsignedValue <= 1_000_000_000_000UL)
            {
                return new GuessCandidate(
                    new RttiFieldDefinition
                    {
                        Name = $"u64_{offset:X4}",
                        Offset = offset,
                        Type = RttiNodeType.UInt64,
                        Comment = "疑似 8 字节无符号整数"
                    },
                    8,
                    54);
            }

            if (signedValue < 0 && signedValue >= -1_000_000_000_000L)
            {
                return new GuessCandidate(
                    new RttiFieldDefinition
                    {
                        Name = $"i64_{offset:X4}",
                        Offset = offset,
                        Type = RttiNodeType.Int64,
                        Comment = "疑似 8 字节有符号整数"
                    },
                    8,
                    54);
            }
        }

        if (offset % 4 == 0 && offset + 4 <= memory.Length)
        {
            uint unsignedValue = BinaryPrimitives.ReadUInt32LittleEndian(
                memory.AsSpan(offset, 4));
            int signedValue = BinaryPrimitives.ReadInt32LittleEndian(memory.AsSpan(offset, 4));
            if (unsignedValue != 0 && unsignedValue <= 10_000_000)
            {
                return new GuessCandidate(
                    new RttiFieldDefinition
                    {
                        Name = signedValue >= 0
                            ? $"u32_{offset:X4}"
                            : $"i32_{offset:X4}",
                        Offset = offset,
                        Type = signedValue >= 0
                            ? RttiNodeType.UInt32
                            : RttiNodeType.Int32,
                        Comment = signedValue >= 0
                            ? "疑似 4 字节无符号整数"
                            : "疑似 4 字节有符号整数"
                    },
                    4,
                    unsignedValue <= byte.MaxValue ? 50 : 56);
            }
        }

        if (offset % 2 == 0 && offset + 2 <= memory.Length)
        {
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(memory.AsSpan(offset, 2));
            if (value > 1)
            {
                return new GuessCandidate(
                    new RttiFieldDefinition
                    {
                        Name = $"u16_{offset:X4}",
                        Offset = offset,
                        Type = RttiNodeType.UInt16,
                        Comment = "疑似 2 字节无符号整数"
                    },
                    2,
                    49);
            }
        }

        if (memory[offset] > 1)
        {
            return new GuessCandidate(
                new RttiFieldDefinition
                {
                    Name = $"u8_{offset:X4}",
                    Offset = offset,
                    Type = RttiNodeType.UInt8,
                    Comment = "疑似单字节数值"
                },
                1,
                48);
        }

        return GuessCandidate.Invalid;
    }

    private static GuessCandidate TryReadBoolean(byte[] memory, int offset)
    {
        if (memory[offset] > 1)
        {
            return GuessCandidate.Invalid;
        }

        if (offset + 4 <= memory.Length &&
            memory[offset + 1] == 0 &&
            memory[offset + 2] == 0 &&
            memory[offset + 3] == 0)
        {
            return GuessCandidate.Invalid;
        }

        return new GuessCandidate(
            new RttiFieldDefinition
            {
                Name = $"bool_{offset:X4}",
                Offset = offset,
                Type = RttiNodeType.Bool,
                Comment = "疑似布尔值"
            },
            1,
            52);
    }

    private static bool TryDecodeInlineUtf16(
        ReadOnlySpan<byte> data,
        out int length,
        out string text)
    {
        length = 0;
        text = string.Empty;
        int limit = Math.Min(data.Length & ~1, MaximumStringLength);
        StringBuilder builder = new();
        for (int offset = 0; offset + 1 < limit; offset += 2)
        {
            char value = (char)BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
            if (value == '\0')
            {
                if (builder.Length < 4 || !IsMostlyPrintable(builder.ToString()))
                {
                    return false;
                }

                length = offset + 2;
                text = TrimPreview(builder.ToString());
                return true;
            }

            if (!IsPrintable(value))
            {
                return false;
            }

            builder.Append(value);
        }

        return false;
    }

    private static bool TryDecodeInlineUtf8(
        ReadOnlySpan<byte> data,
        out int length,
        out string text)
    {
        length = 0;
        text = string.Empty;
        int limit = Math.Min(data.Length, MaximumStringLength);
        int terminator = data[..limit].IndexOf((byte)0);
        if (terminator < 4)
        {
            return false;
        }

        try
        {
            string decoded = StrictUtf8.GetString(data[..terminator]);
            if (!IsMostlyPrintable(decoded))
            {
                return false;
            }

            length = terminator + 1;
            text = TrimPreview(decoded);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsMostlyPrintable(string text)
    {
        if (text.Length < 4)
        {
            return false;
        }

        int printable = text.Count(IsPrintable);
        return printable >= Math.Ceiling(text.Length * 0.85);
    }

    private static bool IsPrintable(char value) =>
        value == '\t' || (!char.IsControl(value) && value != '\uFFFD');

    private static string TrimPreview(string text) =>
        text.Length > 24 ? text[..24] + "..." : text;

    private static bool IsIdentityMatrix(IReadOnlyList<float> values)
    {
        int matches = 0;
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                float expected = row == column ? 1f : 0f;
                if (NearlyEqual(values[row * 4 + column], expected, 0.001f))
                {
                    matches++;
                }
            }
        }

        return matches >= 14;
    }

    private static bool IsTransformMatrix(IReadOnlyList<float> values)
    {
        if (!NearlyEqual(values[3], 0f, 0.01f) ||
            !NearlyEqual(values[7], 0f, 0.01f) ||
            !NearlyEqual(values[11], 0f, 0.01f) ||
            !NearlyEqual(values[15], 1f, 0.01f))
        {
            return false;
        }

        return IsUnitVector(values, 0) &&
               IsUnitVector(values, 4) &&
               IsUnitVector(values, 8);
    }

    private static bool IsUnitVector(IReadOnlyList<float> values, int offset)
    {
        float length = MathF.Sqrt(
            values[offset] * values[offset] +
            values[offset + 1] * values[offset + 1] +
            values[offset + 2] * values[offset + 2]);
        return length is >= 0.001f and <= 1000f;
    }

    private static bool NearlyEqual(float left, float right, float tolerance) =>
        MathF.Abs(left - right) <= tolerance;

    private static bool HasFraction(double value) =>
        Math.Abs(value - Math.Truncate(value)) > 1e-6;

    private static int CountZeroBytes(byte[] memory, int offset)
    {
        int count = 0;
        while (offset + count < memory.Length && memory[offset + count] == 0)
        {
            count++;
        }

        return count;
    }

    private static void AddHexFields(
        RttiClassDefinition definition,
        ReadOnlySpan<byte> data,
        int offset,
        bool isPadding)
    {
        int cursor = 0;
        while (cursor < data.Length)
        {
            int remaining = data.Length - cursor;
            int size = remaining >= 8
                ? 8
                : remaining >= 4
                    ? 4
                    : remaining >= 2
                        ? 2
                        : 1;
            definition.Fields.Add(CreateHexField(
                data.Slice(cursor, size),
                offset + cursor,
                size,
                isPadding));
            cursor += size;
        }
    }

    private static RttiFieldDefinition CreateHexField(
        byte data,
        int offset,
        int size,
        bool isPadding) =>
        CreateHexField(
            new[] { data },
            offset,
            size,
            isPadding);

    private static RttiFieldDefinition CreateHexField(
        ReadOnlySpan<byte> data,
        int offset,
        int size,
        bool isPadding)
    {
        RttiNodeType type = size switch
        {
            2 => RttiNodeType.Hex16,
            4 => RttiNodeType.Hex32,
            8 => RttiNodeType.Hex64,
            _ => RttiNodeType.Hex8
        };
        return new RttiFieldDefinition
        {
            Name = $"{(isPadding ? "pad" : "hex")}_{offset:X4}",
            Offset = offset,
            Type = type,
            Comment = isPadding
                ? "连续 0 字节，按填充保留"
                : "低置信度数据，保留原始字节"
        };
    }

    private readonly record struct GuessCandidate(
        RttiFieldDefinition Field,
        int Size,
        int Score)
    {
        public static GuessCandidate Invalid { get; } = new(
            new RttiFieldDefinition(),
            0,
            0);
    }
}
