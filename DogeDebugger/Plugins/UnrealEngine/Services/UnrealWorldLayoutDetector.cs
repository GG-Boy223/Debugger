using System.Buffers.Binary;
using DogeDebugger.Core.Process;
using DogeDebugger.Plugins.UnrealEngine.Models;

namespace DogeDebugger.Plugins.UnrealEngine.Services;

internal sealed class UnrealWorldLayoutDetector
{
    private const int MaximumWorldOffset = 512;
    private const int MaximumActorArrayOffset = 768;
    private const int MaximumActorSample = 32;
    private readonly ITargetProcess _process;
    private readonly UnrealOffsets _offsets;
    private readonly UnrealNameResolver _names;
    private readonly IReadOnlyList<UnrealObjectInfo> _objects;
    private readonly IReadOnlyDictionary<ulong, UnrealObjectInfo> _byAddress;
    private readonly IReadOnlyDictionary<string, UnrealObjectInfo> _byName;

    public UnrealWorldLayoutDetector(
        ITargetProcess process,
        UnrealOffsets offsets,
        UnrealNameResolver names,
        IReadOnlyList<UnrealObjectInfo> objects)
    {
        _process = process;
        _offsets = offsets;
        _names = names;
        _objects = objects;
        _byAddress = objects
            .GroupBy(item => item.Address)
            .ToDictionary(group => group.Key, group => group.First());
        _byName = objects
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.Ordinal);
    }

    public bool TryFindWorldObject(out UnrealObjectInfo? worldObject)
    {
        worldObject = _objects.FirstOrDefault(item =>
            item.Address != 0 &&
            !item.Name.StartsWith("Default__", StringComparison.Ordinal) &&
            IsClassDerivedFrom(item.ClassAddress, "World"));
        return worldObject is not null;
    }

    public bool TryFindEngineObject(out UnrealObjectInfo? engineObject)
    {
        UnrealObjectInfo[] candidates = _objects
            .Where(item =>
                item.Address != 0 &&
                !item.Name.StartsWith("Default__", StringComparison.Ordinal) &&
                IsClassDerivedFrom(item.ClassAddress, "GameEngine"))
            .ToArray();
        engineObject = candidates.FirstOrDefault(item =>
                            item.Name.Equals("Engine", StringComparison.Ordinal)) ??
                        candidates.FirstOrDefault();
        return engineObject is not null;
    }

    public bool TryDetectWorldLayout(
        CancellationToken cancellationToken,
        out string message)
    {
        message = string.Empty;
        UnrealObjectInfo[] worlds = _objects
            .Where(item =>
                item.Address != 0 &&
                !item.Name.StartsWith("Default__", StringComparison.Ordinal) &&
                IsClassDerivedFrom(item.ClassAddress, "World"))
            .ToArray();
        if (worlds.Length == 0)
        {
            return false;
        }

        foreach (UnrealObjectInfo world in worlds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int levelOffset = 40;
                 levelOffset <= MaximumWorldOffset;
                 levelOffset += 8)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryReadPointer(
                        world.Address + (uint)levelOffset,
                        out ulong level) ||
                    level == 0 ||
                    !_byAddress.TryGetValue(level, out UnrealObjectInfo? levelObject) ||
                    !IsClassDerivedFrom(levelObject.ClassAddress, "Level"))
                {
                    continue;
                }

                for (int actorOffset = 40;
                     actorOffset <= MaximumActorArrayOffset;
                     actorOffset += 8)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryValidateActorArray(
                            level + (uint)actorOffset,
                            out int count,
                            out bool hasWorldSettings))
                    {
                        continue;
                    }

                    _offsets.UWorldPersistentLevel = levelOffset;
                    _offsets.ULevelActors = actorOffset;
                    message =
                        $"PersistentLevel=0x{levelOffset:X}，Actors=0x{actorOffset:X}，{count:N0} 个槽位。";
                    return true;
                }
            }
        }

        return false;
    }

    private bool TryValidateActorArray(
        ulong arrayAddress,
        out int count,
        out bool hasWorldSettings)
    {
        count = 0;
        hasWorldSettings = false;
        if (!TryReadPointer(arrayAddress, out ulong data) ||
            data == 0 ||
            !TryReadInt32(
                arrayAddress + (uint)_offsets.PointerSize,
                out count) ||
            !TryReadInt32(
                arrayAddress + (uint)_offsets.PointerSize + 4,
                out int maximum) ||
            count < 1 ||
            count > 100_000 ||
            maximum < count ||
            maximum > 1_000_000)
        {
            return false;
        }

        int sampleCount = Math.Min(count, MaximumActorSample);
        int valid = 0;
        for (int index = 0; index < sampleCount; index++)
        {
            if (!TryReadPointer(
                    data + (uint)(index * _offsets.PointerSize),
                    out ulong actorAddress) ||
                actorAddress == 0 ||
                !_byAddress.TryGetValue(
                    actorAddress,
                    out UnrealObjectInfo? actor) ||
                !IsClassDerivedFrom(actor.ClassAddress, "Actor"))
            {
                return false;
            }

            valid++;
            hasWorldSettings |= actor.ClassName.Contains(
                "WorldSettings",
                StringComparison.OrdinalIgnoreCase);
        }

        return valid >= 1 && hasWorldSettings;
    }

    private bool IsClassDerivedFrom(ulong classAddress, string baseClassName)
    {
        HashSet<ulong> visited = [];
        int depth = 0;
        while (classAddress != 0 && visited.Add(classAddress) && depth < 64)
        {
            if (_byAddress.TryGetValue(
                    classAddress,
                    out UnrealObjectInfo? classObject))
            {
                if (classObject.Name.Equals(
                        baseClassName,
                        StringComparison.OrdinalIgnoreCase) ||
                    classObject.Name.Contains(
                        baseClassName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (_names.TryResolveName(
                         classAddress + (uint)_offsets.UObjectName,
                         out string className) &&
                     (className.Equals(
                          baseClassName,
                          StringComparison.OrdinalIgnoreCase) ||
                      className.Contains(
                          baseClassName,
                          StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (!TryReadPointer(
                    classAddress + (uint)_offsets.UStructSuperStruct,
                    out classAddress))
            {
                break;
            }

            depth++;
        }

        return false;
    }

    private bool TryReadPointer(ulong address, out ulong value)
    {
        value = 0;
        int size = _offsets.PointerSize is 4 or 8
            ? _offsets.PointerSize
            : _process.Is64Bit ? 8 : 4;
        Span<byte> bytes = stackalloc byte[8];
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
