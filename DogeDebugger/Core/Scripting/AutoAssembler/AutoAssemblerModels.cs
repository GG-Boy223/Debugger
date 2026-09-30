namespace DogeDebugger.Core.Scripting.AutoAssembler;

/// <summary>
/// Result of compiling and executing one Auto Assembler section.
/// </summary>
public sealed class AutoAssemblerResult
{
    public bool Success { get; init; }

    public string ErrorMessage { get; init; } = string.Empty;

    public int ErrorLine { get; init; } = -1;

    public IReadOnlyList<string> Trace { get; init; } = [];

    public AutoAssemblerSessionState? DisableState { get; init; }
}

/// <summary>
/// State captured while enabling a script. The disable pass uses it to
/// resolve allocation addresses that only exist at runtime.
/// </summary>
public sealed class AutoAssemblerSessionState
{
    public List<AutoAssemblerAllocation> Allocations { get; } = [];

    public Dictionary<string, ulong> Symbols { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> RegisteredSymbols { get; } = [];
}

public sealed class AutoAssemblerAllocation
{
    public string Name { get; init; } = string.Empty;

    public ulong Address { get; init; }

    public int Size { get; init; }
}

internal readonly record struct AutoAssemblerScriptLine(string Text, int LineNumber);

/// <summary>
/// Raised when a script line cannot be compiled or executed. Mirrors the
/// original engine's line-aware errors.
/// </summary>
public sealed class AutoAssemblerException : Exception
{
    public AutoAssemblerException(string message, int lineNumber = -1, string? line = null)
        : base(message)
    {
        LineNumber = lineNumber;
        Line = line;
    }

    public int LineNumber { get; }

    public string? Line { get; }
}
