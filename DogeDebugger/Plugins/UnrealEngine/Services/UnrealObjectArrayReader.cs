using System.Buffers.Binary;
using DogeDebugger.Core.Process;
using DogeDebugger.Plugins.UnrealEngine.Models;

namespace DogeDebugger.Plugins.UnrealEngine.Services;

internal sealed class UnrealObjectArrayReader
{
    private const int MaximumObjects = 2_000_000;
    private readonly ITargetProcess _process;
    private readonly UnrealOffsets _offsets;
    private readonly UnrealNameResolver _names;

    public UnrealObjectArrayReader(
        ITargetProcess process,
        UnrealOffsets offsets,
        UnrealNameResolver names)
    {
        _process = process;
        _offsets = offsets;
        _names = names;
    }

    public bool TryReadCount(out int count)
    {
        count = 0;
        return TryReadInt32(
            _offsets.GObjectsAddress + (uint)_offsets.ObjectArrayNumOffset,
            out count) &&
            count is > 0 and <= MaximumObjects;
    }

    public IReadOnlyList<UnrealObjectInfo> ReadObjects(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        maximumCount = Math.Clamp(maximumCount, 1, MaximumObjects);
        List<RawObject> raw = [];
        for (int index = 0; index < maximumCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadItem(index, out ulong objectAddress) || objectAddress == 0)
            {
                continue;
            }

            int objectIndex = TryReadInt32(
                objectAddress + (uint)_offsets.UObjectIndex,
                out int storedIndex)
                ? storedIndex
                : index;
            if (!TryReadPointer(
                    objectAddress + (uint)_offsets.UObjectClass,
                    out ulong classAddress))
            {
                classAddress = 0;
            }

            if (!TryReadPointer(
                    objectAddress + (uint)_offsets.UObjectOuter,
                    out ulong outerAddress))
            {
                outerAddress = 0;
            }

            string name = TryResolveObjectName(objectAddress, out string resolvedName)
                ? resolvedName
                : string.Empty;
            string className = TryResolveObjectName(classAddress, out string resolvedClass)
                ? resolvedClass
                : string.Empty;
            raw.Add(new RawObject(
                objectIndex,
                objectAddress,
                classAddress,
                outerAddress,
                name,
                className));
        }

        Dictionary<ulong, RawObject> byAddress = raw
            .GroupBy(item => item.Address)
            .ToDictionary(group => group.Key, group => group.First());
        List<UnrealObjectInfo> result = new(raw.Count);
        foreach (RawObject item in raw)
        {
            string packageName = ResolvePackageName(item, byAddress);
            string pathName = BuildPathName(item, byAddress);
            result.Add(new UnrealObjectInfo
            {
                Index = item.Index,
                Address = item.Address,
                ClassAddress = item.ClassAddress,
                OuterAddress = item.OuterAddress,
                Name = item.Name,
                ClassName = item.ClassName,
                OuterName = byAddress.TryGetValue(item.OuterAddress, out RawObject? outer)
                    ? outer.Name
                    : string.Empty,
                PackageName = packageName,
                PathName = pathName,
                FullName = string.IsNullOrWhiteSpace(pathName)
                    ? item.Name
                    : $"{pathName}.{item.Name}",
                Kind = Classify(item.ClassName)
            });
        }

        return result;
    }

    private bool TryReadItem(int index, out ulong objectAddress)
    {
        objectAddress = 0;
        if (_offsets.ObjectArrayKind == UnrealObjectArrayKind.Fixed)
        {
            if (!TryReadPointer(
                    _offsets.GObjectsAddress + (uint)_offsets.ObjectArrayObjectsOffset,
                    out ulong objects) ||
                objects == 0)
            {
                return false;
            }

            ulong itemAddress = objects + (uint)(index * _offsets.FUObjectItemSize);
            return TryReadPointer(
                itemAddress + (uint)_offsets.FUObjectItemObjectOffset,
                out objectAddress);
        }

        if (_offsets.ObjectArrayKind != UnrealObjectArrayKind.Chunked ||
            _offsets.ObjectArrayChunkSize <= 0 ||
            !TryReadPointer(
                _offsets.GObjectsAddress + (uint)_offsets.ObjectArrayObjectsOffset,
                out ulong chunks) ||
            chunks == 0)
        {
            return false;
        }

        int chunkIndex = index / _offsets.ObjectArrayChunkSize;
        int itemIndex = index % _offsets.ObjectArrayChunkSize;
        if (!TryReadPointer(chunks + (uint)(chunkIndex * 8), out ulong chunk) ||
            chunk == 0)
        {
            return false;
        }

        ulong chunkItem = chunk + (uint)(itemIndex * _offsets.FUObjectItemSize);
        return TryReadPointer(
            chunkItem + (uint)_offsets.FUObjectItemObjectOffset,
            out objectAddress);
    }

    private bool TryResolveObjectName(ulong objectAddress, out string name)
    {
        name = string.Empty;
        return objectAddress != 0 &&
               _names.TryResolveName(
                   objectAddress + (uint)_offsets.UObjectName,
                   out name);
    }

    private static string ResolvePackageName(
        RawObject item,
        IReadOnlyDictionary<ulong, RawObject> byAddress)
    {
        HashSet<ulong> visited = [];
        RawObject current = item;
        while (current.OuterAddress != 0 &&
               byAddress.TryGetValue(current.OuterAddress, out RawObject? outer) &&
               visited.Add(current.OuterAddress))
        {
            current = outer;
        }

        return current.Name;
    }

    private static string BuildPathName(
        RawObject item,
        IReadOnlyDictionary<ulong, RawObject> byAddress)
    {
        List<string> parts = [];
        HashSet<ulong> visited = [];
        RawObject? current = item;
        while (current is not null &&
               current.OuterAddress != 0 &&
               visited.Add(current.OuterAddress) &&
               byAddress.TryGetValue(current.OuterAddress, out RawObject? outer))
        {
            parts.Add(outer.Name);
            current = outer;
        }

        parts.Reverse();
        return string.Join(".", parts);
    }

    private static UnrealObjectKind Classify(string className)
    {
        if (className.Contains("Package", StringComparison.OrdinalIgnoreCase))
        {
            return UnrealObjectKind.Package;
        }

        if (className.Contains("ScriptStruct", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("Struct", StringComparison.OrdinalIgnoreCase))
        {
            return UnrealObjectKind.Struct;
        }

        if (className.Contains("Function", StringComparison.OrdinalIgnoreCase))
        {
            return UnrealObjectKind.Function;
        }

        if (className.Contains("Enum", StringComparison.OrdinalIgnoreCase))
        {
            return UnrealObjectKind.Enum;
        }

        if (className.Equals("Class", StringComparison.OrdinalIgnoreCase) ||
            className.Contains("BlueprintGeneratedClass", StringComparison.OrdinalIgnoreCase))
        {
            return UnrealObjectKind.Class;
        }

        return UnrealObjectKind.Unknown;
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

    private sealed record RawObject(
        int Index,
        ulong Address,
        ulong ClassAddress,
        ulong OuterAddress,
        string Name,
        string ClassName);
}
