namespace DogeDebugger.Plugins.UnrealEngine.Models;

public sealed class UnrealScanOptions
{
    public string? ModuleName { get; init; }

    public bool UseCache { get; init; } = true;

    public bool AutoDetect { get; init; } = true;

    public bool PersistCache { get; init; } = true;

    public ulong NamePoolAddress { get; init; }

    public ulong NamePoolBlockArrayAddress { get; init; }

    public int FNameEntryStride { get; init; }

    public int FNameBlockOffsetBits { get; init; }

    public ulong GObjectsAddress { get; init; }

    public ulong GWorldAddress { get; init; }

    public ulong GEngineAddress { get; init; }

    public UnrealObjectArrayKind ObjectArrayKind { get; init; }
}
