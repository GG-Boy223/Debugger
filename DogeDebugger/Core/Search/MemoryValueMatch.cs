namespace DogeDebugger.Core.Search;

public sealed class MemoryValueMatch
{
    public required ulong Address { get; init; }

    public required byte[] PreviousBytes { get; init; }

    public required byte[] CurrentBytes { get; init; }

    public required string DisplayValue { get; init; }

    public string Description { get; init; } = string.Empty;

    public string AddressText => $"0x{Address:X}";

    public string PreviousValueText => Convert.ToHexString(PreviousBytes);
}
