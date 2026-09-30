using System.Buffers.Binary;
using DogeDebugger.Core.Memory;
using DogeDebugger.Core.Modules;
using DogeDebugger.Core.Process;
using DogeDebugger.Plugins.UnrealEngine.Models;

namespace DogeDebugger.Plugins.UnrealEngine.Services;

internal sealed class UnrealAutoDetector
{
    private const int ScanChunkSize = 4 * 1024 * 1024;
    private const int ScanOverlap = 32;
    private static readonly byte[] NamePoolPattern =
    [
        0x00, 0x01, 0x4E, 0x6F, 0x6E, 0x65,
        0x00, 0x03, 0x42, 0x79, 0x74, 0x65
    ];

    private readonly ITargetProcess _process;
    private readonly IReadOnlyList<MemoryRegionInfo> _regions;

    public UnrealAutoDetector(
        ITargetProcess process,
        IReadOnlyList<MemoryRegionInfo> regions)
    {
        _process = process;
        _regions = regions;
    }

    public bool TryFindNamePool(
        ModuleDescriptor module,
        CancellationToken cancellationToken,
        out UnrealOffsets? offsets)
    {
        offsets = null;
        foreach (MemoryRegionInfo region in GetScanRegions(module))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ulong cursor = region.BaseAddress;
            ulong end = region.BaseAddress + region.Size;
            while (cursor < end)
            {
                int requested = (int)Math.Min(
                    ScanChunkSize,
                    end - cursor);
                byte[] bytes = new byte[requested];
                int read = _process.ReadBytesPartial(cursor, bytes);
                if (read <= 0)
                {
                    break;
                }

                read = Math.Min(read, bytes.Length);
                if (bytes.Length < NamePoolPattern.Length)
                {
                    break;
                }

                int searchLimit = Math.Max(0, read - NamePoolPattern.Length);
                for (int index = 0; index <= searchLimit; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!bytes.AsSpan(index, NamePoolPattern.Length)
                        .SequenceEqual(NamePoolPattern))
                    {
                        continue;
                    }

                    ulong entryAddress = cursor + (uint)index;
                    if (TryBuildNamePool(entryAddress, out offsets))
                    {
                        return true;
                    }
                }

                if (read < requested)
                {
                    break;
                }

                cursor += (uint)Math.Max(read - ScanOverlap, 1);
            }
        }

        return false;
    }

    public bool TryFindGObjects(
        ModuleDescriptor module,
        UnrealNameResolver names,
        CancellationToken cancellationToken,
        out UnrealOffsets? offsets)
    {
        offsets = null;
        foreach (MemoryRegionInfo region in GetScanRegions(module))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ulong cursor = region.BaseAddress;
            ulong end = region.BaseAddress + region.Size;
            while (cursor < end)
            {
                int requested = (int)Math.Min(
                    ScanChunkSize,
                    end - cursor);
                byte[] bytes = new byte[requested];
                int read = _process.ReadBytesPartial(cursor, bytes);
                if (read <= 0)
                {
                    break;
                }

                read = Math.Min(read, bytes.Length);
                int limit = Math.Max(0, read - 48);
                for (int index = 0; index <= limit; index += 4)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ulong candidate = cursor + (uint)index;
                    if (TryValidateFixed(candidate, names, out offsets) ||
                        TryValidateChunked(candidate, names, out offsets))
                    {
                        return true;
                    }
                }

                if (read < requested)
                {
                    break;
                }

                cursor += (uint)Math.Max(read - 48, 1);
            }
        }

        return false;
    }

    public bool TryFindGlobalPointer(
        ModuleDescriptor module,
        ulong targetAddress,
        CancellationToken cancellationToken,
        out ulong pointerAddress)
    {
        pointerAddress = 0;
        byte[] needle = new byte[_process.Is64Bit ? 8 : 4];
        if (_process.Is64Bit)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(needle, targetAddress);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                needle,
                checked((uint)targetAddress));
        }

        IEnumerable<MemoryRegionInfo> regions = GetScanRegions(module).Take(1);
        foreach (MemoryRegionInfo region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ulong cursor = region.BaseAddress;
            ulong end = region.BaseAddress + region.Size;
            while (cursor < end)
            {
                int requested = (int)Math.Min(ScanChunkSize, end - cursor);
                byte[] bytes = new byte[requested];
                int read = _process.ReadBytesPartial(cursor, bytes);
                if (read <= 0)
                {
                    break;
                }

                read = Math.Min(read, bytes.Length);
                int limit = Math.Max(0, read - needle.Length);
                for (int index = 0; index <= limit; index++)
                {
                    if (!bytes.AsSpan(index, needle.Length).SequenceEqual(needle))
                    {
                        continue;
                    }

                    pointerAddress = cursor + (uint)index;
                    return true;
                }

                if (read < requested)
                {
                    break;
                }

                cursor += (uint)Math.Max(read - needle.Length, 1);
            }
        }

        return false;
    }

    private bool TryBuildNamePool(ulong entryAddress, out UnrealOffsets? offsets)
    {
        offsets = null;
        foreach (int requireByte in new[] { 1, 0 })
        {
            foreach (int stride in new[] { 4, 6, 8, 2 })
            {
                UnrealOffsets candidate = new()
                {
                    PointerSize = _process.Is64Bit ? 8 : 4,
                    NamePoolKind = UnrealNamePoolKind.FNamePool,
                    NamePoolAddress = entryAddress,
                    NamePoolFirstBlockAddress = entryAddress,
                    FNameEntryStride = stride,
                    FNameBlockOffsetBits = 16
                };
                UnrealNameResolver resolver = new(_process, candidate);
                if (!resolver.TryResolveIndex(0, out string name) ||
                    !string.Equals(name, "None", StringComparison.Ordinal))
                {
                    continue;
                }

                if (requireByte == 1)
                {
                    if (!resolver.TryResolveIndex(1, out string second) ||
                        !string.Equals(second, "Byte", StringComparison.Ordinal))
                    {
                        continue;
                    }
                }

                offsets = candidate;
                return true;
            }
        }

        foreach (ulong blockArray in FindPointerReferences(
                     entryAddress,
                     maxResults: 64))
        {
            UnrealOffsets candidate = new()
            {
                PointerSize = _process.Is64Bit ? 8 : 4,
                NamePoolKind = UnrealNamePoolKind.FNamePool,
                NamePoolAddress = blockArray >= 16 ? blockArray - 16 : blockArray,
                NamePoolBlockArrayAddress = blockArray,
                NamePoolFirstBlockAddress = entryAddress,
                FNameEntryStride = 2,
                FNameBlockOffsetBits = 16
            };
            UnrealNameResolver resolver = new(_process, candidate);
            if (resolver.TryResolveIndex(0, out string name) &&
                string.Equals(name, "None", StringComparison.Ordinal) &&
                resolver.TryResolveIndex(1, out string second) &&
                string.Equals(second, "Byte", StringComparison.Ordinal))
            {
                offsets = candidate;
                return true;
            }
        }

        return false;
    }

    private bool TryValidateFixed(
        ulong candidate,
        UnrealNameResolver names,
        out UnrealOffsets? offsets)
    {
        offsets = null;
        if (!TryReadPointer(candidate, out ulong objects) ||
            objects == 0 ||
            !TryReadInt32(candidate + 16, out int maximum) ||
            !TryReadInt32(candidate + 20, out int count) ||
            !IsPlausibleObjectCount(count, maximum))
        {
            return false;
        }

        UnrealOffsets detected = new()
        {
            PointerSize = _process.Is64Bit ? 8 : 4,
            GObjectsAddress = candidate,
            ObjectArrayKind = UnrealObjectArrayKind.Fixed,
            ObjectArrayObjectsOffset = 0,
            ObjectArrayMaxOffset = 16,
            ObjectArrayNumOffset = 20,
            FUObjectItemSize = 24,
            FUObjectItemObjectOffset = 0
        };
        if (!ValidateObjectSamples(objects, count, detected, names))
        {
            return false;
        }

        offsets = detected;
        return true;
    }

    private bool TryValidateChunked(
        ulong candidate,
        UnrealNameResolver names,
        out UnrealOffsets? offsets)
    {
        offsets = null;
        if (!TryReadPointer(candidate, out ulong chunks) ||
            chunks == 0 ||
            !TryReadInt32(candidate + 16, out int maximum) ||
            !TryReadInt32(candidate + 20, out int count) ||
            !TryReadInt32(candidate + 24, out int maximumChunks) ||
            !TryReadInt32(candidate + 28, out int chunkCount) ||
            !IsPlausibleObjectCount(count, maximum) ||
            maximumChunks is < 1 or > 4096 ||
            chunkCount is < 1 ||
            chunkCount > maximumChunks)
        {
            return false;
        }

        UnrealOffsets detected = new()
        {
            PointerSize = _process.Is64Bit ? 8 : 4,
            GObjectsAddress = candidate,
            ObjectArrayKind = UnrealObjectArrayKind.Chunked,
            ObjectArrayObjectsOffset = 0,
            ObjectArrayMaxOffset = 16,
            ObjectArrayNumOffset = 20,
            ObjectArrayMaxChunksOffset = 24,
            ObjectArrayNumChunksOffset = 28,
            ObjectArrayChunkSize = 65536,
            FUObjectItemSize = 24,
            FUObjectItemObjectOffset = 0
        };
        if (!ValidateObjectSamples(chunks, count, detected, names))
        {
            return false;
        }

        offsets = detected;
        return true;
    }

    private bool ValidateObjectSamples(
        ulong firstPointer,
        int count,
        UnrealOffsets offsets,
        UnrealNameResolver names)
    {
        int sampleCount = Math.Clamp(count, 1, 16);
        int valid = 0;
        for (int index = 0; index < sampleCount; index++)
        {
            ulong itemAddress;
            if (offsets.ObjectArrayKind == UnrealObjectArrayKind.Fixed)
            {
                itemAddress = firstPointer +
                              (uint)(index * offsets.FUObjectItemSize);
            }
            else
            {
                int chunkIndex = index / offsets.ObjectArrayChunkSize;
                int itemIndex = index % offsets.ObjectArrayChunkSize;
                if (!TryReadPointer(
                        firstPointer + (uint)(chunkIndex * 8),
                        out ulong chunk) ||
                    chunk == 0)
                {
                    return false;
                }

                itemAddress = chunk +
                              (uint)(itemIndex * offsets.FUObjectItemSize);
            }

            if (!TryReadPointer(
                    itemAddress + (uint)offsets.FUObjectItemObjectOffset,
                    out ulong objectAddress) ||
                objectAddress == 0 ||
                !TryReadInt32(
                    objectAddress + (uint)offsets.UObjectIndex,
                    out int storedIndex) ||
                storedIndex < 0)
            {
                continue;
            }

            if (!TryReadPointer(
                    objectAddress + (uint)offsets.UObjectClass,
                    out ulong classAddress) ||
                classAddress == 0)
            {
                continue;
            }

            if (names.TryResolveName(
                    objectAddress + (uint)offsets.UObjectName,
                    out string objectName) &&
                names.TryResolveName(
                    classAddress + (uint)offsets.UObjectName,
                    out string className) &&
                !string.IsNullOrWhiteSpace(objectName) &&
                !string.IsNullOrWhiteSpace(className))
            {
                valid++;
            }
        }

        return valid >= Math.Max(1, sampleCount / 2);
    }

    private IEnumerable<ulong> FindPointerReferences(
        ulong value,
        int maxResults)
    {
        byte[] needle = new byte[_process.Is64Bit ? 8 : 4];
        if (_process.Is64Bit)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(needle, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(needle, checked((uint)value));
        }

        int found = 0;
        foreach (MemoryRegionInfo region in _regions)
        {
            if (!region.IsReadable || region.Size > 64UL * 1024 * 1024)
            {
                continue;
            }

            ulong cursor = region.BaseAddress;
            ulong end = region.BaseAddress + region.Size;
            while (cursor < end && found < maxResults)
            {
                int requested = (int)Math.Min(ScanChunkSize, end - cursor);
                byte[] bytes = new byte[requested];
                int read = _process.ReadBytesPartial(cursor, bytes);
                if (read <= 0)
                {
                    break;
                }

                read = Math.Min(read, bytes.Length);
                int limit = Math.Max(0, read - needle.Length);
                for (int index = 0; index <= limit && found < maxResults; index++)
                {
                    if (!bytes.AsSpan(index, needle.Length).SequenceEqual(needle))
                    {
                        continue;
                    }

                    found++;
                    yield return cursor + (uint)index;
                }

                if (read < requested)
                {
                    break;
                }

                cursor += (uint)Math.Max(read - needle.Length, 1);
            }
        }
    }

    private IEnumerable<MemoryRegionInfo> GetScanRegions(ModuleDescriptor module)
    {
        yield return new MemoryRegionInfo
        {
            BaseAddress = module.BaseAddress,
            Size = module.Size,
            Protect = 0x02,
            ProtectText = "R"
        };

        foreach (MemoryRegionInfo region in _regions)
        {
            if (!region.IsReadable ||
                region.Size == 0 ||
                region.Size > 256UL * 1024 * 1024)
            {
                continue;
            }

            if (region.BaseAddress < module.BaseAddress + module.Size &&
                region.BaseAddress + region.Size > module.BaseAddress)
            {
                continue;
            }

            yield return region;
        }
    }

    private static bool IsPlausibleObjectCount(int count, int maximum) =>
        count >= 100 &&
        count <= 50_000_000 &&
        maximum >= count &&
        maximum <= 50_000_000;

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

    private bool TryReadInt32(ulong address, out int value)
    {
        value = 0;
        Span<byte> bytes = stackalloc byte[4];
        if (!_process.TryReadBytes(address, bytes))
        {
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        return true;
    }
}
