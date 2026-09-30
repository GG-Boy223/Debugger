namespace DogeDebugger.Core.StringRef;

public sealed class StringEntry
{
    public required ulong Address { get; init; }

    public required int ByteLength { get; init; }

    public required string Text { get; init; }

    public required string EncodingName { get; init; }

    public IReadOnlyList<ulong> References { get; set; } = [];

    public string AddressText => $"0x{Address:X}";

    public int ReferenceCount => References.Count;

    public bool IsUnicode =>
        EncodingName.Contains("Unicode", StringComparison.OrdinalIgnoreCase) ||
        EncodingName.Contains("UTF-16", StringComparison.OrdinalIgnoreCase);
}
