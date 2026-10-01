namespace DogeDebugger.Core.Disassembly;

public sealed class InstructionSnapshot
{
    public ulong Address { get; init; }

    public int Length { get; init; }

    public byte[] Bytes { get; init; } = [];

    public string Text { get; init; } = string.Empty;

    public string Mnemonic { get; init; } = string.Empty;

    public string Operands { get; init; } = string.Empty;

    public string FlowControl { get; init; } = string.Empty;

    public ulong? NearBranchTarget { get; init; }

    public IReadOnlyList<ulong> ReferencedAddresses { get; init; } = [];

    public IReadOnlyList<ulong> ImmediateValues { get; init; } = [];

    public IReadOnlyList<ulong> AbsoluteMemoryAddresses { get; init; } = [];

    public IReadOnlyList<byte> FixedByteMask { get; init; } = [];

    public string DisplayAddressText { get; set; } = string.Empty;

    public string DisplayBytesText { get; init; } = string.Empty;

    public string Comment { get; set; } = string.Empty;

    public string CustomSymbolName { get; set; } = string.Empty;

    public string CommentText =>
        string.IsNullOrWhiteSpace(Comment)
            ? string.Empty
            : $"  ; {Comment}";

    public string ArrowText { get; init; } = string.Empty;

    public bool IsInstructionPointer { get; set; }

    public bool IsBreakpoint { get; set; }

    public string MarkerText => IsBreakpoint
        ? "●"
        : IsInstructionPointer
            ? "▶"
            : ArrowText;

    public string AddressText =>
        string.IsNullOrWhiteSpace(DisplayAddressText)
            ? $"{Address:X16}"
            : DisplayAddressText;

    public string BytesText =>
        string.IsNullOrWhiteSpace(DisplayBytesText)
            ? string.Join(' ', Bytes.Select(static value => value.ToString("X2")))
            : DisplayBytesText;

    public string OperandText => string.IsNullOrWhiteSpace(Operands)
        ? Text
        : $"{Mnemonic} {Operands}";

    public bool IsCall => string.Equals(FlowControl, "Call", StringComparison.Ordinal);

    public bool IsJump => string.Equals(FlowControl, "UnconditionalBranch", StringComparison.Ordinal);

    public bool IsReturn =>
        string.Equals(FlowControl, "Return", StringComparison.Ordinal) ||
        string.Equals(FlowControl, "IndirectBranch", StringComparison.Ordinal);

    public string DisplayInstructionText =>
        string.IsNullOrWhiteSpace(CustomSymbolName)
            ? Text
            : $"{CustomSymbolName}  ; {Text}";

    public override string ToString() => $"{Address:X16}  {Text}";
}
