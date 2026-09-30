namespace DogeDebugger.Debugger.Scripting;

public sealed class LuaScriptExecutionResult
{
    public required bool Succeeded { get; init; }

    public required string OutputText { get; init; }

    public required string ErrorText { get; init; }

    public required TimeSpan Duration { get; init; }

    public IReadOnlyList<object?> ReturnValues { get; init; } = [];
}
