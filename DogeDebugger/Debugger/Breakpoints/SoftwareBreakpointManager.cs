using System.Collections.Concurrent;
using DogeDebugger.Core.Process;
using DogeDebugger.Debugger.UserMode;

namespace DogeDebugger.Debugger.Breakpoints;

public sealed class SoftwareBreakpointManager : IDisposable
{
    private const byte BreakpointInstruction = 0xCC;

    private readonly ITargetProcess _process;
    private readonly ConcurrentDictionary<ulong, BreakpointEntry> _breakpoints = [];
    private bool _disposed;

    public SoftwareBreakpointManager(ITargetProcess process)
    {
        _process = process;
    }

    public event Action<BreakpointEntry>? BreakpointChanged;

    public IReadOnlyList<BreakpointEntry> Entries =>
        _breakpoints.Values.OrderBy(static breakpoint => breakpoint.Address).ToArray();

    public bool Contains(ulong address) => _breakpoints.ContainsKey(address);

    public BreakpointEntry? Find(ulong address) =>
        _breakpoints.TryGetValue(address, out BreakpointEntry? entry) ? entry : null;

    public bool Add(
        ulong address,
        bool temporary = false,
        byte? originalByte = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (address == 0)
        {
            return false;
        }

        BreakpointEntry entry = new()
        {
            Address = address,
            Kind = BreakpointKind.Software,
            IsEnabled = true,
            IsTemporary = temporary,
            OriginalByte = originalByte ?? 0,
            HasOriginalByte = originalByte.HasValue
        };

        if (!_breakpoints.TryAdd(address, entry))
        {
            return false;
        }

        if (TryWriteBreakpoint(entry))
        {
            BreakpointChanged?.Invoke(entry);
            return true;
        }

        _breakpoints.TryRemove(address, out _);
        return false;
    }

    public bool Remove(ulong address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_breakpoints.TryGetValue(address, out BreakpointEntry? entry))
        {
            return false;
        }

        if (!TryRestoreOriginal(entry))
        {
            return false;
        }

        if (!_breakpoints.TryRemove(address, out _))
        {
            return false;
        }

        BreakpointChanged?.Invoke(entry);
        return true;
    }

    public bool Disable(ulong address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_breakpoints.TryGetValue(address, out BreakpointEntry? entry) || !entry.IsEnabled)
        {
            return false;
        }

        if (!TryRestoreOriginal(entry))
        {
            return false;
        }

        entry.IsEnabled = false;
        entry.IsArmed = false;
        BreakpointChanged?.Invoke(entry);
        return true;
    }

    public bool Enable(ulong address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_breakpoints.TryGetValue(address, out BreakpointEntry? entry) || entry.IsEnabled)
        {
            return false;
        }

        if (!TryWriteBreakpoint(entry))
        {
            return false;
        }

        entry.IsEnabled = true;
        entry.IsArmed = true;
        BreakpointChanged?.Invoke(entry);
        return true;
    }

    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (BreakpointEntry entry in Entries)
        {
            Remove(entry.Address);
        }
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

    public bool MarkInternal(ulong address, string ownerId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_breakpoints.TryGetValue(address, out BreakpointEntry? entry) ||
            string.IsNullOrWhiteSpace(ownerId))
        {
            return false;
        }

        entry.IsInternal = true;
        entry.InternalOwnerId = ownerId;
        entry.Comment = "InstructionAccessWatch";
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
        entry.HasConditionError = false;
        BreakpointChanged?.Invoke(entry);
    }

    public void SetConditionError(ulong address, bool hasError)
    {
        if (!_breakpoints.TryGetValue(address, out BreakpointEntry? entry))
        {
            return;
        }

        if (entry.HasConditionError == hasError)
        {
            return;
        }

        entry.HasConditionError = hasError;
        BreakpointChanged?.Invoke(entry);
    }

    public bool HandleHit(ulong exceptionAddress, out BreakpointEntry? entry)
    {
        if (exceptionAddress == 0)
        {
            entry = null;
            return false;
        }

        if (!TryResolveHit(exceptionAddress, out entry) || entry is null)
        {
            entry = null;
            return false;
        }

        if (!TryRestoreOriginal(entry))
        {
            entry = null;
            return false;
        }

        entry.HitCount++;
        entry.IsArmed = false;
        BreakpointChanged?.Invoke(entry);
        return true;
    }

    public bool TryResolveHit(
        ulong exceptionAddress,
        out BreakpointEntry? entry)
    {
        entry = null;
        if (exceptionAddress == 0)
        {
            return false;
        }

        if (_breakpoints.TryGetValue(exceptionAddress, out entry) &&
            entry.IsEnabled)
        {
            return true;
        }

        ulong adjusted = exceptionAddress - 1;
        if (adjusted != 0 &&
            _breakpoints.TryGetValue(adjusted, out entry) &&
            entry.IsEnabled)
        {
            return true;
        }

        entry = null;
        return false;
    }

    public void ArmAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (BreakpointEntry entry in _breakpoints.Values)
        {
            if (entry.IsEnabled)
            {
                TryWriteBreakpoint(entry);
            }
        }
    }

    public void RestoreAll()
    {
        foreach (BreakpointEntry entry in _breakpoints.Values)
        {
            TryRestoreOriginal(entry);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        RestoreAll();
        _breakpoints.Clear();
        _disposed = true;
    }

    private bool TryWriteBreakpoint(BreakpointEntry entry)
    {
        if (!entry.HasOriginalByte)
        {
            Span<byte> original = stackalloc byte[1];
            if (!_process.TryReadBytes(entry.Address, original))
            {
                return false;
            }

            entry.OriginalByte = original[0];
            entry.HasOriginalByte = true;
        }

        Span<byte> patch = stackalloc byte[1] { BreakpointInstruction };
        if (!_process.TryWriteBytes(entry.Address, patch))
        {
            return false;
        }

        entry.IsArmed = true;

        FlushInstructionCache(entry.Address);
        return true;
    }

    private bool TryRestoreOriginal(BreakpointEntry entry)
    {
        if (!entry.HasOriginalByte)
        {
            return false;
        }

        Span<byte> original = stackalloc byte[1] { entry.OriginalByte };
        if (!_process.TryWriteBytes(entry.Address, original))
        {
            return false;
        }

        entry.IsArmed = false;
        FlushInstructionCache(entry.Address);
        return true;
    }

    private void FlushInstructionCache(ulong address)
    {
        NativeDebuggerMethods.FlushInstructionCache(
            _process.Handle,
            unchecked((IntPtr)(long)address),
            UIntPtr.Zero);
    }
}
