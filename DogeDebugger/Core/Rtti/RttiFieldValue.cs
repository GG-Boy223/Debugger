namespace DogeDebugger.Core.Rtti;

public sealed record RttiFieldValue(
    RttiFieldDefinition Field,
    ulong Address,
    string Value,
    bool IsReadable)
{
    public string OffsetText => $"0x{Field.Offset:X4}";

    public string AddressText => $"0x{Address:X}";

    public string TypeText => Field.Type.ToString();

    public string Name => Field.Name;

    public string Comment => Field.Comment;
}
