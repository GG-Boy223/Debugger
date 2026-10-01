using System.Collections.Concurrent;

namespace DogeDebugger.Debugger.Breakpoints;

public sealed class HardwareBreakpointManager : IDisposable
{
    private readonly ConcurrentDictionary<ulong, BreakpointEntry> _breakpoints = [];
    private bool _disposed;

    public event Action<BreakpointEntry>? BreakpointChanged;

    public event Action? Changed;

    public IReadOnlyList<BreakpointEntry> Entries =>
        _breakpoints.Values.OrderBy(static breakpoint => breakpoint.Address).ToArray();

    public BreakpointEntry? Find(ulong address) =>
        _breakpoints.TryGetValue(address, out BreakpointEntry? entry) ? entry : null;

    public bool Add(
        ulong address,
        BreakpointKind kind,
        string? condition = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (address == 0 || kind == BreakpointKind.Software)
        {
            return false;
        }

        if (_breakpoints.Count >= 4 && !_breakpoints.ContainsKey(address))
        {
            return false;
        }

        BreakpointEntry entry = new()
        {
            Address = address,
            Kind = kind,
            IsEnabled = true,
            IsArmed = true,
            Condition = condition?.Trim() ?? string.Empty
        };
        if (!_breakpoints.TryAdd(address, entry))
        {
            return false;
        }

        BreakpointChanged?.Invoke(entry);
        Changed?.Invoke();
        return true;
    }

    public bool Remove(ulong address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_breakpoints.TryRemove(address, out BreakpointEntry? entry))
        {
            return false;
        }

        entry.IsEnabled = false;
        entry.IsArmed = false;
        BreakpointChanged?.Invoke(entry);
        Changed?.Invoke();
        return true;
    }

    public bool Disable(ulong address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_breakpoints.TryGetValue(address, out BreakpointEntry? entry) ||
            !entry.IsEnabled)
        {
            return false;
        }

        entry.IsEnabled = false;
        entry.IsArmed = false;
        BreakpointChanged?.Invoke(entry);
        Changed?.Invoke();
        return true;
    }

    public bool Enable(ulong address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_breakpoints.TryGetValue(address, out BreakpointEntry? entry) ||
            entry.IsEnabled)
        {
            return false;
        }

        entry.IsEnabled = true;
        entry.IsArmed = true;
        BreakpointChanged?.Invoke(entry);
        Changed?.Invoke();
        return true;
    }

    public bool UpdateCondition(ulong address, string? condition)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_breakpoints.TryGetValue(address, out BreakpointEntry? entry))
        {
            return false;
        }

        entry.Condition = condition?.Trim() ?? string.Empty;
        entry.ConditionMissCount = 0;
        entry.HasConditionError = false;
        BreakpointChanged?.Invoke(entry);
        return true;
    }

    public void RecordConditionMiss(ulong address)
    {
        if (!_breakpoints.TryGetValue(address, out BreakpointEntry? entry))
        {
            return;
        }

        entry.ConditionMissCount++;
        BreakpointChanged?.Invoke(entry);
    }

    public void RecordHit(ulong address)
    {
        if (!_breakpoints.TryGetValue(address, out BreakpointEntry? entry))
        {
            return;
        }

        entry.HitCount++;
        BreakpointChanged?.Invoke(entry);
    }

    public void Clear()
    {
        foreach (BreakpointEntry entry in Entries)
        {
            Remove(entry.Address);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Clear();
        _disposed = true;
    }
}
