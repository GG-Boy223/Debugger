using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;

namespace DogeDebugger.Plugins.CEMono.Protocol;

internal sealed class MonoPipeClient : IDisposable
{
    private const int DefaultConnectTimeoutMilliseconds = 5000;
    private const int DefaultOperationTimeoutMilliseconds = 30000;

    private readonly NamedPipeClientStream _pipe;
    private readonly int _operationTimeoutMilliseconds;
    private readonly object _sync = new();
    private bool _disposed;

    public MonoPipeClient(
        int processId,
        int connectTimeoutMilliseconds = DefaultConnectTimeoutMilliseconds,
        int operationTimeoutMilliseconds = DefaultOperationTimeoutMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentOutOfRangeException.ThrowIfNegative(connectTimeoutMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(operationTimeoutMilliseconds);

        _operationTimeoutMilliseconds = operationTimeoutMilliseconds;
        _pipe = new NamedPipeClientStream(
            ".",
            $"cemonodc_pid{processId}",
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        _pipe.Connect(connectTimeoutMilliseconds);
        _pipe.ReadMode = PipeTransmissionMode.Byte;
    }

    public bool IsConnected => !_disposed && _pipe.IsConnected;

    public void SendCommand(MonoDataCollectorCommand command)
    {
        lock (_sync)
        {
            WriteByteCore((byte)command);
        }
    }

    public void WriteByte(byte value)
    {
        lock (_sync)
        {
            WriteByteCore(value);
        }
    }

    public void WriteWord(ushort value)
    {
        lock (_sync)
        {
            Span<byte> buffer = stackalloc byte[sizeof(ushort)];
            BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
            WriteCore(buffer);
        }
    }

    public void WriteDWord(uint value)
    {
        lock (_sync)
        {
            Span<byte> buffer = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
            WriteCore(buffer);
        }
    }

    public void WriteQWord(ulong value)
    {
        lock (_sync)
        {
            Span<byte> buffer = stackalloc byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
            WriteCore(buffer);
        }
    }

    public void WriteString(string? value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        if (bytes.Length > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Mono protocol strings cannot exceed 65535 UTF-8 bytes.");
        }

        lock (_sync)
        {
            Span<byte> length = stackalloc byte[sizeof(ushort)];
            BinaryPrimitives.WriteUInt16LittleEndian(
                length,
                checked((ushort)bytes.Length));
            WriteCore(length);
            WriteCore(bytes);
        }
    }

    public byte ReadByte() =>
        ReadExact(sizeof(byte))[0];

    public ushort ReadWord()
    {
        byte[] bytes = ReadExact(sizeof(ushort));
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes);
    }

    public uint ReadDWord()
    {
        byte[] bytes = ReadExact(sizeof(uint));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    public int ReadInt32()
    {
        byte[] bytes = ReadExact(sizeof(int));
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    public ulong ReadQWord()
    {
        byte[] bytes = ReadExact(sizeof(ulong));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    public string ReadString()
    {
        int length = ReadWord();
        return length == 0
            ? string.Empty
            : Encoding.UTF8.GetString(ReadExact(length));
    }

    public string ReadByteLengthString()
    {
        int length = ReadByte();
        return length == 0
            ? string.Empty
            : Encoding.UTF8.GetString(ReadExact(length));
    }

    public byte[] ReadBytes(int length)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        return length == 0 ? [] : ReadExact(length);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pipe.Dispose();
        }
    }

    private byte[] ReadExact(int length)
    {
        lock (_sync)
        {
            byte[] buffer = new byte[length];
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = ReadCore(buffer.AsMemory(offset));
                if (read <= 0)
                {
                    throw new IOException("Mono DataCollector pipe closed unexpectedly.");
                }

                offset += read;
            }

            return buffer;
        }
    }

    private int ReadCore(Memory<byte> buffer)
    {
        EnsureUsable();
        Task<int> readTask = _pipe.ReadAsync(buffer).AsTask();
        try
        {
            return _operationTimeoutMilliseconds == 0
                ? readTask.GetAwaiter().GetResult()
                : readTask
                    .WaitAsync(TimeSpan.FromMilliseconds(_operationTimeoutMilliseconds))
                    .GetAwaiter()
                    .GetResult();
        }
        catch (TimeoutException)
        {
            Dispose();
            throw new TimeoutException(
                $"Mono DataCollector pipe read timed out after " +
                $"{_operationTimeoutMilliseconds} ms.");
        }
    }

    private void WriteCore(ReadOnlySpan<byte> buffer)
    {
        EnsureUsable();
        byte[] data = buffer.ToArray();
        ValueTask writeTask = _pipe.WriteAsync(data);
        try
        {
            if (_operationTimeoutMilliseconds != 0)
            {
                writeTask.AsTask()
                    .WaitAsync(TimeSpan.FromMilliseconds(_operationTimeoutMilliseconds))
                    .GetAwaiter()
                    .GetResult();
            }
            else
            {
                writeTask.GetAwaiter().GetResult();
            }
        }
        catch (TimeoutException)
        {
            Dispose();
            throw new TimeoutException(
                $"Mono DataCollector pipe write timed out after " +
                $"{_operationTimeoutMilliseconds} ms.");
        }
    }

    private void WriteByteCore(byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        buffer[0] = value;
        WriteCore(buffer);
    }

    private void EnsureUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_pipe.IsConnected)
        {
            throw new IOException("Mono DataCollector pipe is not connected.");
        }
    }
}
