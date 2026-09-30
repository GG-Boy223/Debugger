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

    public string AddressText => $"0x{Address:X}";

    public string BytesText => string.Join(' ', Bytes.Select(static value => value.ToString("X2")));

    public string OperandText => string.IsNullOrWhiteSpace(Operands)
        ? Text
        : $"{Mnemonic} {Operands}";

    public bool IsCall => string.Equals(FlowControl, "Call", StringComparison.Ordinal);

    public bool IsJump => string.Equals(FlowControl, "UnconditionalBranch", StringComparison.Ordinal);

    public bool IsReturn =>
        string.Equals(FlowControl, "Return", StringComparison.Ordinal) ||
        string.Equals(FlowControl, "IndirectBranch", StringComparison.Ordinal);

    public override string ToString() => $"{Address:X16}  {Text}";
}
