namespace DogeDebugger.Plugins.UnrealEngine.Models;

public sealed class UnrealDiagnosticEntry
{
    public required string Stage { get; init; }

    public required UnrealDiagnosticLevel Level { get; init; }

    public required string Message { get; init; }

    public string LevelText => Level switch
    {
        UnrealDiagnosticLevel.Success => "成功",
        UnrealDiagnosticLevel.Warning => "降级",
        _ => "失败"
    };
}
