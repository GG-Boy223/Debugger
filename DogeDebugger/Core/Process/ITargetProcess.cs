using System.ComponentModel;

namespace DogeDebugger.Core.Process;

public interface ITargetProcess : INotifyPropertyChanged, IDisposable
{
    int ProcessId { get; }

    string ProcessName { get; }

    string FilePath { get; }

    IntPtr Handle { get; }

    bool IsOpen { get; }

    bool Is64Bit { get; }

    bool HasMemory { get; }

    bool IsDebugging { get; }

    event Action<ITargetProcess>? Opened;

    event Action? Closed;

    event Action? DebuggerAttached;

    event Action? DebuggerDetached;

    bool Open(int processId, string processName, string filePath);

    bool OpenFromHandle(int processId, string processName, string filePath, IntPtr processHandle);

    void Close();

    bool TryReadBytes(ulong address, Span<byte> buffer);

    byte[] ReadBytes(ulong address, int size);

    int ReadBytesPartial(ulong address, Span<byte> buffer);

    bool TryWriteBytes(ulong address, ReadOnlySpan<byte> bytes);
}
