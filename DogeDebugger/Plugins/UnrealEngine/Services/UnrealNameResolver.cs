using System.Buffers.Binary;
using System.Text;
using DogeDebugger.Core.Process;
using DogeDebugger.Plugins.UnrealEngine.Models;

namespace DogeDebugger.Plugins.UnrealEngine.Services;

internal sealed class UnrealNameResolver
{
    private const int LegacyNameChunkSize = 16384;
    private readonly ITargetProcess _process;
    private readonly UnrealOffsets _offsets;
    private readonly Dictionary<int, string> _cache = [];

    public UnrealNameResolver(ITargetProcess process, UnrealOffsets offsets)
    {
        _process = process;
        _offsets = offsets;
    }

    public bool TryResolveIndex(int index, out string name)
    {
        if (index < 0)
        {
            name = string.Empty;
            return false;
        }

        if (_cache.TryGetValue(index, out string? cached))
        {
            name = cached;
            return true;
        }

        bool resolved = _offsets.NamePoolKind == UnrealNamePoolKind.LegacyGNames
            ? TryResolveLegacy(index, out name)
            : TryResolveNamePool(index, out name);
        if (resolved)
        {
            _cache[index] = name;
        }

        return resolved;
    }

    public bool TryResolveName(ulong nameAddress, out string name)
    {
        name = string.Empty;
        if (!TryReadUInt32(nameAddress, out uint value))
        {
            return false;
        }

        int index = unchecked((int)(value & 0x3FFFFFFF));
        return TryResolveIndex(index, out name);
    }

    private bool TryResolveNamePool(int index, out string name)
    {
        name = string.Empty;
        int blockBits = Math.Clamp(_offsets.FNameBlockOffsetBits, 12, 20);
        int blockIndex = index >> blockBits;
        int blockOffset = index & ((1 << blockBits) - 1);
        ulong block;
        if (blockIndex == 0 && _offsets.NamePoolFirstBlockAddress != 0)
        {
            block = _offsets.NamePoolFirstBlockAddress;
        }
        else
        {
            ulong blockArray = _offsets.NamePoolBlockArrayAddress != 0
                ? _offsets.NamePoolBlockArrayAddress
                : _offsets.NamePoolAddress + 16;
            int pointerSize = _offsets.PointerSize is 4 or 8
                ? _offsets.PointerSize
                : _process.Is64Bit ? 8 : 4;
            if (!TryReadPointer(
                    blockArray + (uint)(blockIndex * pointerSize),
                    out block))
            {
                return false;
            }
        }

        if (block == 0)
        {
            return false;
        }

        int stride = 2 + Math.Clamp(_offsets.FNameEntryStride, 0, 32);
        ulong entry = block + (uint)(blockOffset * stride);
        if (!TryReadUInt16(entry, out ushort header))
        {
            return false;
        }

        bool wide = (header & 1) != 0;
        int length = header >> 6;
        if (length < 0 || length > 1024)
        {
            return false;
        }

        int bytesLength = length * (wide ? 2 : 1);
        byte[] bytes = new byte[bytesLength];
        if (!_process.TryReadBytes(entry + 2, bytes))
        {
            return false;
        }

        name = wide
            ? Encoding.Unicode.GetString(bytes).TrimEnd('\0')
            : Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        return name.Length > 0;
    }

    private bool TryResolveLegacy(int index, out string name)
    {
        name = string.Empty;
        ulong names = _offsets.NamePoolAddress;
        if (_offsets.LegacyNamePoolIsIndirect &&
            (!TryReadPointer(names, out names) || names == 0))
        {
            return false;
        }

        int chunkIndex = index / LegacyNameChunkSize;
        int entryIndex = index % LegacyNameChunkSize;
        if (!TryReadPointer(names + (uint)(chunkIndex * 8), out ulong chunk) ||
            chunk == 0 ||
            !TryReadPointer(chunk + (uint)(entryIndex * 8), out ulong entry) ||
            entry == 0)
        {
            return false;
        }

        ulong stringAddress = entry + (uint)Math.Max(
            _offsets.LegacyNameEntryStringOffset,
            0);
        byte[] bytes = new byte[1024];
        int read = _process.ReadBytesPartial(stringAddress, bytes);
        if (read <= 0)
        {
            return false;
        }

        int terminator = Array.IndexOf(bytes, (byte)0, 0, read);
        if (terminator <= 0)
        {
            return false;
        }

        name = Encoding.UTF8.GetString(bytes, 0, terminator);
        return name.Length > 0;
    }

    private bool TryReadPointer(ulong address, out ulong value)
    {
        value = 0;
        Span<byte> bytes = stackalloc byte[8];
        int size = _process.Is64Bit ? 8 : 4;
        if (!_process.TryReadBytes(address, bytes[..size]))
        {
            return false;
        }

        value = size == 8
            ? BinaryPrimitives.ReadUInt64LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return true;
    }

    private bool TryReadUInt16(ulong address, out ushort value)
    {
        value = 0;
        Span<byte> bytes = stackalloc byte[2];
        if (!_process.TryReadBytes(address, bytes))
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        return true;
    }

    private bool TryReadUInt32(ulong address, out uint value)
    {
        value = 0;
        Span<byte> bytes = stackalloc byte[4];
        if (!_process.TryReadBytes(address, bytes))
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return true;
    }
}
