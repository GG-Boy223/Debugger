namespace DogeDebugger.Core.Signatures;

public sealed class AobSignature
{
    public required string Pattern { get; init; }

    public required byte[] Bytes { get; init; }

    public required bool[] Mask { get; init; }

    public int MatchCount { get; init; }

    public int Length => Bytes.Length;
}
