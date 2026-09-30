namespace DogeDebugger.Core.Rtti;

public sealed class RttiFieldDefinition
{
    public string Name { get; set; } = string.Empty;

    public string Comment { get; set; } = string.Empty;

    public int Offset { get; set; }

    public RttiNodeType Type { get; set; }

    public int Length { get; set; }

    public int PointerSize { get; set; }

    public RttiNodeType? PointedType { get; set; }

    public string? PointedClass { get; set; }

    public int? ArrayIndex { get; set; }

    public int GetSize(bool is64Bit)
    {
        return Type switch
        {
            RttiNodeType.Hex8 or
            RttiNodeType.Int8 or
            RttiNodeType.UInt8 or
            RttiNodeType.Bool => 1,
            RttiNodeType.Hex16 or
            RttiNodeType.Int16 or
            RttiNodeType.UInt16 => 2,
            RttiNodeType.Hex32 or
            RttiNodeType.Int32 or
            RttiNodeType.UInt32 or
            RttiNodeType.Float => 4,
            RttiNodeType.Hex64 or
            RttiNodeType.Int64 or
            RttiNodeType.UInt64 or
            RttiNodeType.Double => 8,
            RttiNodeType.Pointer => PointerSize is 4 or 8
                ? PointerSize
                : is64Bit ? 8 : 4,
            RttiNodeType.Vector2 => 8,
            RttiNodeType.Vector3 => 12,
            RttiNodeType.Vector4 => 16,
            RttiNodeType.Matrix4x4 => 64,
            RttiNodeType.Utf8Text or RttiNodeType.Utf16Text => Math.Max(Length, 0),
            _ => Math.Max(Length, 0)
        };
    }
}
