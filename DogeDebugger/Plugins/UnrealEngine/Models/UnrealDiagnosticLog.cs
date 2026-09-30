namespace DogeDebugger.Plugins.UnrealEngine.Models;

public sealed class UnrealDiagnosticLog
{
    private readonly object _gate = new();
    private readonly List<UnrealDiagnosticEntry> _entries = [];

    public IReadOnlyList<UnrealDiagnosticEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public bool HasProblems
    {
        get
        {
            lock (_gate)
            {
                return _entries.Any(entry => entry.Level != UnrealDiagnosticLevel.Success);
            }
        }
    }

    public void Success(string stage, string message) =>
        Add(stage, UnrealDiagnosticLevel.Success, message);

    public void Warning(string stage, string message) =>
        Add(stage, UnrealDiagnosticLevel.Warning, message);

    public void Failure(string stage, string message) =>
        Add(stage, UnrealDiagnosticLevel.Failure, message);

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    private void Add(string stage, UnrealDiagnosticLevel level, string message)
    {
        lock (_gate)
        {
            _entries.Add(new UnrealDiagnosticEntry
            {
                Stage = stage,
                Level = level,
                Message = message
            });
        }
    }
}
