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

    public bool Add(ulong address, bool temporary = false)
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
            IsTemporary = temporary
        };

        if (!_breakpoints.TryAdd(address, entry))
        {
            return false;
        }

        if (TryWriteBreakpoint(address))
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
        if (!_breakpoints.TryRemove(address, out BreakpointEntry? entry))
        {
            return false;
        }

        TryRestoreOriginal(entry);
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

        if (!TryWriteBreakpoint(address))
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

    public bool HandleHit(ulong exceptionAddress, out BreakpointEntry? entry)
    {
        if (exceptionAddress == 0)
        {
            entry = null;
            return false;
        }

        ulong address = exceptionAddress - 1;
        if (!_breakpoints.TryGetValue(address, out entry) || !entry.IsEnabled)
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

    public void ArmAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (BreakpointEntry entry in _breakpoints.Values)
        {
            if (entry.IsEnabled)
            {
                TryWriteBreakpoint(entry.Address);
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

    private bool TryWriteBreakpoint(ulong address)
    {
        Span<byte> original = stackalloc byte[1];
        if (!_process.TryReadBytes(address, original))
        {
            return false;
        }

        BreakpointEntry? entry = Find(address);
        if (entry is not null)
        {
            entry.OriginalByte = original[0];
        }

        Span<byte> patch = stackalloc byte[1] { BreakpointInstruction };
        if (!_process.TryWriteBytes(address, patch))
        {
            return false;
        }

        if (entry is not null)
        {
            entry.IsArmed = true;
        }

        FlushInstructionCache(address);
        return true;
    }

    private bool TryRestoreOriginal(BreakpointEntry entry)
    {
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
