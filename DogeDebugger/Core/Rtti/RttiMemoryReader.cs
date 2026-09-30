using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using DogeDebugger.Core.Process;

namespace DogeDebugger.Core.Rtti;

public static class RttiMemoryReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static RttiInstance ReadInstance(
        ITargetProcess process,
        RttiClassDefinition classDefinition,
        ulong address)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(classDefinition);

        RttiInstance instance = new()
        {
            ClassDefinition = classDefinition,
            BaseAddress = address
        };
        if (!process.IsOpen || address == 0)
        {
            instance.SetError(address == 0 ? "地址为 0" : "目标进程未打开");
            return instance;
        }

        int size = classDefinition.GetSize(process.Is64Bit);
        if (size <= 0)
        {
            instance.SetMemory([]);
            return instance;
        }

        byte[] memory = new byte[size];
        if (!process.TryReadBytes(address, memory))
        {
            instance.SetError($"无法完整读取 0x{address:X} 的 {size} 字节。");
            return instance;
        }

        instance.SetMemory(memory);
        return instance;
    }

    public static IReadOnlyList<RttiFieldValue> ReadValues(
        ITargetProcess process,
        RttiInstance instance)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(instance);

        List<RttiFieldValue> values = new(instance.ClassDefinition.Fields.Count);
        foreach (RttiFieldDefinition field in instance.ClassDefinition.Fields)
        {
            ulong address = instance.BaseAddress + checked((uint)Math.Max(field.Offset, 0));
            int size = field.GetSize(process.Is64Bit);
            if (!instance.HasMemory ||
                field.Offset < 0 ||
                size < 0 ||
                field.Offset > instance.MemoryBuffer.Length - size)
            {
                values.Add(new RttiFieldValue(field, address, "(unreadable)", false));
                continue;
            }

            ReadOnlySpan<byte> data = instance.MemoryBuffer.AsSpan(field.Offset, size);
            values.Add(new RttiFieldValue(
                field,
                address,
                FormatValue(process, field, data),
                true));
        }

        return values;
    }

    public static string FormatValue(
        ITargetProcess process,
        RttiFieldDefinition field,
        ReadOnlySpan<byte> data)
    {
        try
        {
            return field.Type switch
            {
                RttiNodeType.Hex8 => $"0x{data[0]:X2}",
                RttiNodeType.Hex16 => $"0x{BinaryPrimitives.ReadUInt16LittleEndian(data):X4}",
                RttiNodeType.Hex32 => $"0x{BinaryPrimitives.ReadUInt32LittleEndian(data):X8}",
                RttiNodeType.Hex64 => $"0x{BinaryPrimitives.ReadUInt64LittleEndian(data):X16}",
                RttiNodeType.Int8 => unchecked((sbyte)data[0]).ToString(CultureInfo.InvariantCulture),
                RttiNodeType.Int16 => BinaryPrimitives.ReadInt16LittleEndian(data)
                    .ToString(CultureInfo.InvariantCulture),
                RttiNodeType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(data)
                    .ToString(CultureInfo.InvariantCulture),
                RttiNodeType.Int64 => BinaryPrimitives.ReadInt64LittleEndian(data)
                    .ToString(CultureInfo.InvariantCulture),
                RttiNodeType.UInt8 => data[0].ToString(CultureInfo.InvariantCulture),
                RttiNodeType.UInt16 => BinaryPrimitives.ReadUInt16LittleEndian(data)
                    .ToString(CultureInfo.InvariantCulture),
                RttiNodeType.UInt32 => BinaryPrimitives.ReadUInt32LittleEndian(data)
                    .ToString(CultureInfo.InvariantCulture),
                RttiNodeType.UInt64 => BinaryPrimitives.ReadUInt64LittleEndian(data)
                    .ToString(CultureInfo.InvariantCulture),
                RttiNodeType.Float => BinaryPrimitives.ReadSingleLittleEndian(data)
                    .ToString("G9", CultureInfo.InvariantCulture),
                RttiNodeType.Double => BinaryPrimitives.ReadDoubleLittleEndian(data)
                    .ToString("G17", CultureInfo.InvariantCulture),
                RttiNodeType.Bool => data[0] != 0 ? "true" : "false",
                RttiNodeType.Pointer => FormatPointer(process, field, data),
                RttiNodeType.Vector2 => FormatFloats(data, 2),
                RttiNodeType.Vector3 => FormatFloats(data, 3),
                RttiNodeType.Vector4 => FormatFloats(data, 4),
                RttiNodeType.Matrix4x4 => FormatMatrix(data),
                RttiNodeType.Utf8Text => DecodeUtf8(data),
                RttiNodeType.Utf16Text => DecodeUtf16(data),
                RttiNodeType.Class or RttiNodeType.Instance =>
                    field.PointedClass is { Length: > 0 } className
                        ? $"{className} @ 0x{(data.Length >= 8 ? BinaryPrimitives.ReadUInt64LittleEndian(data) : BinaryPrimitives.ReadUInt32LittleEndian(data)):X}"
                        : "(nested value)",
                _ => Convert.ToHexString(data)
            };
        }
        catch (ArgumentException)
        {
            return Convert.ToHexString(data);
        }
    }

    private static string FormatPointer(
        ITargetProcess process,
        RttiFieldDefinition field,
        ReadOnlySpan<byte> data)
    {
        ulong pointer = field.GetSize(process.Is64Bit) == 8
            ? BinaryPrimitives.ReadUInt64LittleEndian(data)
            : BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (pointer == 0)
        {
            return "nullptr";
        }

        string pointedValue = string.Empty;
        if (field.PointedType is RttiNodeType.Utf8Text or RttiNodeType.Utf16Text)
        {
            byte[] pointedBytes = new byte[256];
            int read = process.ReadBytesPartial(pointer, pointedBytes);
            if (read > 0)
            {
                string text = field.PointedType == RttiNodeType.Utf16Text
                    ? DecodeUtf16(pointedBytes.AsSpan(0, read))
                    : DecodeUtf8(pointedBytes.AsSpan(0, read));
                if (text.Length > 48)
                {
                    text = text[..48] + "...";
                }

                pointedValue = $" -> \"{text}\"";
            }
        }
        else if (!string.IsNullOrWhiteSpace(field.PointedClass))
        {
            pointedValue = $" -> {field.PointedClass}";
        }

        return $"0x{pointer:X}{pointedValue}";
    }

    private static string FormatFloats(ReadOnlySpan<byte> data, int count)
    {
        string[] values = new string[count];
        for (int index = 0; index < count; index++)
        {
            values[index] = BinaryPrimitives.ReadSingleLittleEndian(
                    data.Slice(index * sizeof(float), sizeof(float)))
                .ToString("G9", CultureInfo.InvariantCulture);
        }

        return string.Join(", ", values);
    }

    private static string FormatMatrix(ReadOnlySpan<byte> data)
    {
        string[] rows = new string[4];
        for (int row = 0; row < 4; row++)
        {
            rows[row] = FormatFloats(data.Slice(row * 16, 16), 4);
        }

        return string.Join(" | ", rows);
    }

    private static string DecodeUtf8(ReadOnlySpan<byte> data)
    {
        int length = data.IndexOf((byte)0);
        if (length < 0)
        {
            length = data.Length;
        }

        if (length == 0)
        {
            return string.Empty;
        }

        try
        {
            return StrictUtf8.GetString(data[..length]);
        }
        catch (DecoderFallbackException)
        {
            return Convert.ToHexString(data[..length]);
        }
    }

    private static string DecodeUtf16(ReadOnlySpan<byte> data)
    {
        int length = 0;
        while (length + 1 < data.Length &&
               (data[length] != 0 || data[length + 1] != 0))
        {
            length += 2;
        }

        return length == 0
            ? string.Empty
            : Encoding.Unicode.GetString(data[..length]);
    }
}
