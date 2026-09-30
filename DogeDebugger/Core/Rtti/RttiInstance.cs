namespace DogeDebugger.Core.Rtti;

public sealed class RttiInstance
{
    public Guid Id { get; } = Guid.NewGuid();

    public required RttiClassDefinition ClassDefinition { get; init; }

    public required ulong BaseAddress { get; set; }

    public byte[] MemoryBuffer { get; private set; } = [];

    public string? ReadError { get; private set; }

    public long SnapshotVersion { get; private set; }

    public bool HasMemory => ReadError is null && MemoryBuffer.Length > 0;

    public string AddressText => $"0x{BaseAddress:X}";

    public string StatusText => HasMemory ? "读取正常" : ReadError ?? "尚未读取";

    public void SetMemory(ReadOnlySpan<byte> memory)
    {
        MemoryBuffer = memory.ToArray();
        ReadError = null;
        SnapshotVersion++;
    }

    public void SetError(string? error)
    {
        MemoryBuffer = [];
        ReadError = string.IsNullOrWhiteSpace(error) ? "无法读取" : error;
        SnapshotVersion++;
    }
}
