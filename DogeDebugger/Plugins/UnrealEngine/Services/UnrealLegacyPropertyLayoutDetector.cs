using System.Buffers.Binary;
using DogeDebugger.Core.Process;
using DogeDebugger.Plugins.UnrealEngine.Models;

namespace DogeDebugger.Plugins.UnrealEngine.Services;

internal sealed class UnrealLegacyPropertyLayoutDetector
{
    private static readonly (string First, string Second)[] SuperRelations =
    [
        ("Class", "Struct"),
        ("Struct", "Field"),
        ("Field", "Object"),
        ("Function", "Struct"),
        ("Actor", "Object"),
        ("Pawn", "Actor"),
        ("Character", "Pawn")
    ];

    private static readonly Dictionary<string, int> StructSizes = new(StringComparer.Ordinal)
    {
        ["Vector"] = 12,
        ["Vector2D"] = 8,
        ["Rotator"] = 12,
        ["Guid"] = 16,
        ["Color"] = 4
    };

    private static readonly Dictionary<string, (int Offset, int Size)> PropertyLayouts =
        new(StringComparer.Ordinal)
        {
            ["Vector.X"] = (0, 4),
            ["Vector.Y"] = (4, 4),
            ["Vector.Z"] = (8, 4),
            ["Vector2D.X"] = (0, 4),
            ["Vector2D.Y"] = (4, 4),
            ["Rotator.Pitch"] = (0, 4),
            ["Rotator.Yaw"] = (4, 4),
            ["Rotator.Roll"] = (8, 4),
            ["Guid.A"] = (0, 4),
            ["Guid.B"] = (4, 4),
            ["Guid.C"] = (8, 4),
            ["Guid.D"] = (12, 4)
        };

    private readonly ITargetProcess _process;
    private readonly UnrealOffsets _offsets;
    private readonly IReadOnlyList<UnrealObjectInfo> _objects;
    private readonly IReadOnlyDictionary<ulong, UnrealObjectInfo> _byAddress;
    private readonly IReadOnlyDictionary<string, UnrealObjectInfo> _classesByName;
    private readonly UnrealObjectInfo[] _fieldObjects;
    private readonly UnrealObjectInfo[] _structObjects;
    private readonly HashSet<ulong> _fieldAddresses;
    private readonly (UnrealObjectInfo Property, int Offset, int Size)[] _knownProperties;

    public UnrealLegacyPropertyLayoutDetector(
        ITargetProcess process,
        UnrealOffsets offsets,
        IReadOnlyList<UnrealObjectInfo> objects)
    {
        _process = process;
        _offsets = offsets;
        _objects = objects;
        _byAddress = objects
            .GroupBy(item => item.Address)
            .ToDictionary(group => group.Key, group => group.First());
        _classesByName = objects
            .Where(item => item.Kind == UnrealObjectKind.Class)
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.Ordinal);
        _fieldObjects = objects
            .Where(IsPropertyOrFunction)
            .Take(1024)
            .ToArray();
        _fieldAddresses = _fieldObjects
            .Select(item => item.Address)
            .ToHashSet();
        _structObjects = objects
            .Where(item => item.Kind is
                UnrealObjectKind.Class or
                UnrealObjectKind.Struct or
                UnrealObjectKind.Function)
            .Take(2048)
            .ToArray();
        _knownProperties = _fieldObjects
            .Select(property =>
            {
                string key = string.IsNullOrWhiteSpace(property.OuterName)
                    ? property.Name
                    : $"{property.OuterName}.{property.Name}";
                return PropertyLayouts.TryGetValue(
                    key,
                    out (int Offset, int Size) layout)
                        ? (Property: property, Offset: layout.Offset, Size: layout.Size)
                        : (Property: property, Offset: int.MinValue, Size: int.MinValue);
            })
            .Where(item => item.Offset != int.MinValue)
            .ToArray();
    }

    public bool TryDetect(CancellationToken cancellationToken, out string message)
    {
        message = string.Empty;
        if (_fieldObjects.Length == 0 ||
            _structObjects.Length == 0 ||
            _knownProperties.Length == 0)
        {
            return false;
        }

        int superOffset = FindBestOffset(
            40,
            128,
            8,
            3,
            ScoreSuper,
            cancellationToken);
        if (superOffset < 0)
        {
            return false;
        }

        int fieldNextOffset = FindBestOffset(
            40,
            superOffset - 8,
            8,
            4,
            ScoreFieldNext,
            cancellationToken);
        if (fieldNextOffset < 0)
        {
            return false;
        }

        _offsets.UStructSuperStruct = superOffset;
        _offsets.UFieldNext = fieldNextOffset;

        int childrenOffset = FindBestOffset(
            superOffset + 8,
            superOffset + 80,
            8,
            8,
            offset => ScoreChildren(offset, fieldNextOffset, cancellationToken),
            cancellationToken);
        if (childrenOffset < 0)
        {
            return false;
        }

        _offsets.UStructChildren = childrenOffset;
        int propertiesSizeOffset = FindBestOffset(
            childrenOffset + 8,
            childrenOffset + 64,
            4,
            3,
            ScoreStructSize,
            cancellationToken);
        if (propertiesSizeOffset < 0)
        {
            return false;
        }

        _offsets.UStructPropertiesSize = propertiesSizeOffset;
        int minimumAlignmentOffset = FindBestOffset(
            propertiesSizeOffset + 4,
            propertiesSizeOffset + 48,
            4,
            3,
            offset => ScoreMinimumAlignment(offset, propertiesSizeOffset),
            cancellationToken);
        if (minimumAlignmentOffset >= 0)
        {
            _offsets.UStructMinAlignment = minimumAlignmentOffset;
        }

        int arrayDimOffset = FindBestOffset(
            fieldNextOffset + 8,
            fieldNextOffset + 64,
            4,
            4,
            ScoreArrayDim,
            cancellationToken);
        if (arrayDimOffset < 0)
        {
            return false;
        }

        _offsets.FPropertyArrayDim = arrayDimOffset;
        _offsets.FPropertyElementSize = arrayDimOffset + 4;
        _offsets.FPropertyPropertyFlags = arrayDimOffset + 8;
        int propertyOffset = FindBestOffset(
            arrayDimOffset + 16,
            arrayDimOffset + 72,
            4,
            6,
            ScorePropertyOffset,
            cancellationToken);
        if (propertyOffset < 0)
        {
            return false;
        }

        _offsets.FPropertyOffsetInternal = propertyOffset;
        int propertyLinkOffset = FindBestOffset(
            (propertyOffset + 11) & ~7,
            propertyOffset + 56,
            8,
            4,
            ScorePropertyLink,
            cancellationToken);
        if (propertyLinkOffset < 0)
        {
            return false;
        }

        _offsets.FPropertyPropertyLinkNext = propertyLinkOffset;
        int classPropertyLinkOffset = FindBestOffset(
            childrenOffset + 8,
            childrenOffset + 112,
            8,
            8,
            offset => ScoreClassPropertyLink(
                offset,
                propertyLinkOffset,
                cancellationToken),
            cancellationToken);
        if (classPropertyLinkOffset < 0)
        {
            return false;
        }

        _offsets.UClassPropertyLink = classPropertyLinkOffset;
        _offsets.UClassPropertyLinkAlt = classPropertyLinkOffset;
        DetectPropertyMetadataOffsets();
        DetectFunctionLayout(cancellationToken);
        _offsets.UsesLegacyUProperties = true;
        _offsets.UStructChildProperties = -1;

        message =
            $"Super=0x{superOffset:X} Children=0x{childrenOffset:X} " +
            $"UField.Next=0x{fieldNextOffset:X} " +
            $"FProperty.Offset=0x{propertyOffset:X} " +
            $"PropertyLink=0x{classPropertyLinkOffset:X}。";
        return true;
    }

    private void DetectPropertyMetadataOffsets()
    {
        if (!_classesByName.TryGetValue(
                "Property",
                out UnrealObjectInfo? propertyClass) ||
            !TryReadInt32(
                propertyClass.Address + (uint)_offsets.UStructPropertiesSize,
                out int propertySize) ||
            propertySize < _offsets.FPropertyPropertyLinkNext + 8 ||
            propertySize > 384)
        {
            return;
        }

        _offsets.FPropertyBitMaskField = propertySize;
        _offsets.FPropertyObjectClassType = propertySize;
        _offsets.FPropertyEnumField = propertySize + _offsets.PointerSize;
    }

    private void DetectFunctionLayout(CancellationToken cancellationToken)
    {
        if (!_classesByName.TryGetValue(
                "Struct",
                out UnrealObjectInfo? structClass) ||
            !TryReadInt32(
                structClass.Address + (uint)_offsets.UStructPropertiesSize,
                out int functionFlagsOffset) ||
            functionFlagsOffset <= _offsets.UStructChildren ||
            functionFlagsOffset >= 512)
        {
            return;
        }

        _offsets.UFunctionFunctionFlags = functionFlagsOffset;
        UnrealObjectInfo[] functions = _objects
            .Where(item => item.Kind == UnrealObjectKind.Function)
            .Take(256)
            .ToArray();
        int execOffset = FindBestOffset(
            (functionFlagsOffset + 7) & ~7,
            functionFlagsOffset + 96,
            8,
            8,
            offset => functions.Count(function =>
                TryReadPointer(
                    function.Address + (uint)offset,
                    out ulong address) &&
                address > 0x10000),
            cancellationToken);
        if (execOffset >= 0)
        {
            _offsets.UFunctionExecFunction = execOffset;
        }
    }

    private int ScoreSuper(int offset)
    {
        int score = 0;
        foreach ((string firstName, string secondName) in SuperRelations)
        {
            if (!_classesByName.TryGetValue(
                    firstName,
                    out UnrealObjectInfo? first) ||
                !_classesByName.TryGetValue(
                    secondName,
                    out UnrealObjectInfo? second))
            {
                continue;
            }

            if (TryReadPointer(
                    first.Address + (uint)offset,
                    out ulong super) &&
                super == second.Address)
            {
                score++;
            }
        }

        return score;
    }

    private int ScoreFieldNext(int offset)
    {
        int score = 0;
        foreach (UnrealObjectInfo field in _fieldObjects)
        {
            if (!TryReadPointer(
                    field.Address + (uint)offset,
                    out ulong next) ||
                !_byAddress.TryGetValue(next, out UnrealObjectInfo? nextField) ||
                nextField.Address == field.Address ||
                nextField.OuterAddress != field.OuterAddress ||
                !IsPropertyOrFunction(nextField))
            {
                continue;
            }

            score++;
        }

        return score;
    }

    private int ScoreChildren(
        int offset,
        int fieldNextOffset,
        CancellationToken cancellationToken)
    {
        int score = 0;
        foreach (UnrealObjectInfo item in _structObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_fieldAddresses.Contains(item.OuterAddress) ||
                !TryReadPointer(
                    item.Address + (uint)offset,
                    out ulong child))
            {
                continue;
            }

            score += CountFieldChain(
                child,
                fieldNextOffset,
                item.Address,
                propertiesOnly: false);
        }

        return score;
    }

    private int ScoreStructSize(int offset)
    {
        int score = 0;
        foreach ((string name, int expectedSize) in StructSizes)
        {
            if (_classesByName.TryGetValue(name, out UnrealObjectInfo? structObject) &&
                TryReadInt32(
                    structObject.Address + (uint)offset,
                    out int size) &&
                size == expectedSize)
            {
                score++;
            }
        }

        return score;
    }

    private int ScoreMinimumAlignment(int offset, int propertiesSizeOffset)
    {
        int score = 0;
        foreach ((string name, int _) in StructSizes)
        {
            if (!_classesByName.TryGetValue(
                    name,
                    out UnrealObjectInfo? structObject) ||
                !TryReadInt32(
                    structObject.Address + (uint)propertiesSizeOffset,
                    out int size) ||
                !TryReadInt32(
                    structObject.Address + (uint)offset,
                    out int alignment) ||
                alignment <= 0 ||
                alignment > 16 ||
                size % alignment != 0)
            {
                continue;
            }

            score++;
        }

        return score;
    }

    private int ScoreArrayDim(int offset)
    {
        int score = 0;
        foreach ((UnrealObjectInfo property, int _, int expectedSize) in _knownProperties)
        {
            if (TryReadInt32(
                    property.Address + (uint)offset,
                    out int arrayDim) &&
                arrayDim == 1 &&
                TryReadInt32(
                    property.Address + (uint)offset + 4,
                    out int elementSize) &&
                elementSize == expectedSize)
            {
                score++;
            }
        }

        return score;
    }

    private int ScorePropertyOffset(int offset)
    {
        int score = 0;
        foreach ((UnrealObjectInfo property, int expectedOffset, int _) in _knownProperties)
        {
            if (TryReadInt32(
                    property.Address + (uint)offset,
                    out int actualOffset) &&
                actualOffset == expectedOffset)
            {
                score++;
            }
        }

        return score;
    }

    private int ScorePropertyLink(int offset)
    {
        int score = 0;
        foreach (UnrealObjectInfo field in _fieldObjects)
        {
            if (TryReadPointer(
                    field.Address + (uint)offset,
                    out ulong next) &&
                _byAddress.TryGetValue(next, out UnrealObjectInfo? nextField) &&
                nextField.ClassName.Contains(
                    "Property",
                    StringComparison.Ordinal))
            {
                score++;
            }
        }

        return score;
    }

    private int ScoreClassPropertyLink(
        int offset,
        int propertyLinkOffset,
        CancellationToken cancellationToken)
    {
        int score = 0;
        foreach (UnrealObjectInfo item in _structObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadPointer(
                    item.Address + (uint)offset,
                    out ulong property))
            {
                score += CountFieldChain(
                    property,
                    propertyLinkOffset,
                    owner: 0,
                    propertiesOnly: true);
            }
        }

        return score;
    }

    private int CountFieldChain(
        ulong address,
        int linkOffset,
        ulong owner,
        bool propertiesOnly)
    {
        HashSet<ulong> visited = [];
        while (visited.Count < 512 &&
               _byAddress.TryGetValue(
                   address,
                   out UnrealObjectInfo? field) &&
               (!propertiesOnly || IsProperty(field)) &&
               (owner == 0 || field.OuterAddress == owner) &&
               visited.Add(address) &&
               TryReadPointer(
                   address + (uint)linkOffset,
                   out address))
        {
        }

        return visited.Count;
    }

    private int FindBestOffset(
        int start,
        int end,
        int step,
        int minimumScore,
        Func<int, int> score,
        CancellationToken cancellationToken)
    {
        int bestOffset = -1;
        int bestScore = minimumScore - 1;
        for (int offset = start; offset <= end; offset += step)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int current = score(offset);
            if (current > bestScore)
            {
                bestOffset = offset;
                bestScore = current;
            }
        }

        return bestOffset;
    }

    private static bool IsPropertyOrFunction(UnrealObjectInfo item) =>
        IsProperty(item) || item.Kind == UnrealObjectKind.Function;

    private static bool IsProperty(UnrealObjectInfo item) =>
        item.ClassName.Contains("Property", StringComparison.Ordinal);

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
