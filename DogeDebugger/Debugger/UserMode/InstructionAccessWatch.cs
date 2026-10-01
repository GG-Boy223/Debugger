using DogeDebugger.Core.Disassembly;
using DogeDebugger.Debugger.Session;

namespace DogeDebugger.Debugger.UserMode;

public sealed class InstructionAccessWatch : IDisposable
{
    private readonly DebuggerSession _session;
    private readonly InstructionSnapshot _instruction;
    private readonly InstructionMemoryOperand _operand;
    private readonly int _bitness;
    private readonly int _maximumRecords;

    private string? _watchId;
    private InstructionAccessWatchSnapshot? _snapshot;
    private bool _disposed;

    public InstructionAccessWatch(
        DebuggerSession session,
        InstructionSnapshot instruction,
        InstructionMemoryOperand operand,
        int bitness,
        int maximumRecords = 100_000)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _instruction = instruction ?? throw new ArgumentNullException(nameof(instruction));
        _operand = operand ?? throw new ArgumentNullException(nameof(operand));
        _bitness = bitness;
        _maximumRecords = Math.Clamp(maximumRecords, 1, 100_000);
        _session.InstructionAccessWatches.Updated += HandleUpdated;
        _session.InstructionAccessWatches.StateChanged += HandleStateChanged;
    }

    public event Action? Changed;

    public event Action<IReadOnlyList<InstructionAccessWatchRecord>>? RecordsUpdated;

    public string? WatchId => _watchId;

    public bool IsActive =>
        _snapshot?.State == InstructionAccessWatchState.Running;

    public int ValueSize
    {
        get => _snapshot?.ValueSize ?? _operand.DefaultValueSize;
        set
        {
            if (_watchId is null ||
                _session.InstructionAccessWatches.ChangeValueSize(
                    _watchId,
                    value,
                    out _))
            {
                return;
            }
        }
    }

    public IReadOnlyList<InstructionAccessWatchRecord> Records =>
        _snapshot?.Records ?? [];

    public bool Start(out string error)
    {
        ThrowIfDisposed();
        if (_watchId is not null)
        {
            error = string.Empty;
            return IsActive;
        }

        InstructionAccessWatchStartResult result =
            _session.InstructionAccessWatches.Start(
                _instruction,
                _operand,
                _bitness,
                requestedValueSize: null,
                _maximumRecords);
        if (!result.Success || string.IsNullOrWhiteSpace(result.WatchId))
        {
            error = result.Error ?? "Failed to start the instruction access watch.";
            return false;
        }

        _watchId = result.WatchId;
        _session.InstructionAccessWatches.TryGetSnapshot(
            _watchId,
            offset: 0,
            limit: 0,
            out InstructionAccessWatchSnapshot? snapshot);
        _snapshot = snapshot;
        error = string.Empty;
        Changed?.Invoke();
        return true;
    }

    public void Stop()
    {
        if (_watchId is null)
        {
            return;
        }

        _ = _session.InstructionAccessWatches.StopAsync(
            _watchId,
            InstructionAccessResumeMode.WatchOnly);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _session.InstructionAccessWatches.Updated -= HandleUpdated;
        _session.InstructionAccessWatches.StateChanged -= HandleStateChanged;
        GC.SuppressFinalize(this);
    }

    private void HandleUpdated(InstructionAccessWatchSnapshot snapshot)
    {
        if (!string.Equals(
                snapshot.WatchId,
                _watchId,
                StringComparison.Ordinal))
        {
            return;
        }

        _snapshot = snapshot;
        RecordsUpdated?.Invoke(snapshot.Records);
        Changed?.Invoke();
    }

    private void HandleStateChanged(string watchId)
    {
        if (!string.Equals(watchId, _watchId, StringComparison.Ordinal) ||
            !_session.InstructionAccessWatches.TryGetSnapshot(
                watchId,
                offset: 0,
                limit: 0,
                out InstructionAccessWatchSnapshot? snapshot) ||
            snapshot is null)
        {
            return;
        }

        HandleUpdated(snapshot);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}
