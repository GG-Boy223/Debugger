namespace DogeDebugger.Plugins.UnrealEngine.Models;

public sealed class UnrealValueReadOptions
{
    public int MaxStringLength { get; init; } = 256;

    public int ArrayPreview { get; init; } = 8;

    public int StructDepth { get; init; } = 1;
}
