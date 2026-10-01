namespace DogeDebugger.Core.Search;

public sealed class MemoryValueMatch
{
    public required ulong Address { get; init; }

    public MemoryValueKind Kind { get; init; } = MemoryValueKind.Int32;

    public required byte[] PreviousBytes { get; init; }

    public required byte[] CurrentBytes { get; init; }

    public required string DisplayValue { get; init; }

    public string Description { get; init; } = string.Empty;

    public string AddressText => $"0x{Address:X}";

    public string CurrentValue => DisplayValue;

    public string PreviousValue => PreviousValueText;

    public string DisplayAddress { get; set; } = string.Empty;

    public string PreviousValueText { get; set; } = string.Empty;

    public bool IsValueChanged { get; set; }
}
