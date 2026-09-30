using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using DogeDebugger.Core.Process;
using DogeDebugger.Plugins.UnrealEngine.Models;

namespace DogeDebugger.Plugins.UnrealEngine.Services;

internal sealed class UnrealValueReader
{
    private const int MaximumArrayCount = 1_000_000;
    private const int MaximumStructMembers = 256;
    private readonly ITargetProcess _process;
    private readonly UnrealOffsets _offsets;
    private readonly UnrealNameResolver _names;
    private readonly IReadOnlyDictionary<ulong, UnrealObjectInfo> _objects;
    private readonly Func<UnrealObjectInfo, bool, IReadOnlyList<UnrealMemberInfo>> _getMembers;
    private readonly Func<ulong, long, string?> _resolveEnumValue;

    public UnrealValueReader(
        ITargetProcess process,
        UnrealOffsets offsets,
        UnrealNameResolver names,
        IReadOnlyList<UnrealObjectInfo> objects,
        Func<UnrealObjectInfo, bool, IReadOnlyList<UnrealMemberInfo>> getMembers,
        Func<ulong, long, string?> resolveEnumValue)
    {
        _process = process;
        _offsets = offsets;
        _names = names;
        _objects = objects
            .GroupBy(item => item.Address)
            .ToDictionary(group => group.Key, group => group.First());
        _getMembers = getMembers;
        _resolveEnumValue = resolveEnumValue;
    }

    public IReadOnlyList<UnrealMemberValueEntry> ReadObjectValues(
        UnrealObjectInfo objectInfo,
        IReadOnlyList<UnrealMemberInfo> members,
        UnrealValueReadOptions options)
    {
        List<UnrealMemberValueEntry> result = new(members.Count);
        foreach (UnrealMemberInfo member in members)
        {
            result.Add(new UnrealMemberValueEntry
            {
                Member = member,
                TypeName = GetDisplayTypeName(member),
                Value = ReadMemberValue(objectInfo, member, depth: 0, options)
            });
        }

        return result;
    }

    public UnrealMemberValue ReadMemberValue(
        UnrealObjectInfo objectInfo,
        UnrealMemberInfo member,
        int depth,
        UnrealValueReadOptions options)
    {
        if (depth > 4 || member.Offset < 0)
        {
            return Unreadable(
                objectInfo.Address,
                member.TypeName,
                "Unreadable or recursive member.");
        }

        ulong address = objectInfo.Address + (uint)member.Offset;
        if (member.ArrayDim > 1)
        {
            int preview = Math.Clamp(options.ArrayPreview, 0, 64);
            int elementSize = Math.Max(member.Size, 1);
            List<UnrealMemberValue> elements = new(Math.Min(member.ArrayDim, preview));
            for (int index = 0; index < Math.Min(member.ArrayDim, preview); index++)
            {
                UnrealMemberValue element = ReadValue(
                    address + (uint)(index * elementSize),
                    member.TypeName,
                    elementSize,
                    member.Address,
                    depth + 1,
                    options);
                element.DisplayName = $"[{index}] {element.DisplayName}";
                elements.Add(element);
            }

            return new UnrealMemberValue
            {
                Kind = UnrealValueKind.Array,
                DisplayName = $"{GetDisplayTypeName(member)}[{member.ArrayDim}]",
                TypeName = GetDisplayTypeName(member),
                Address = address,
                Children = elements
            };
        }

        return ReadValue(
            address,
            member.TypeName,
            member.Size,
            member.Address,
            depth,
            options);
    }

    public string GetDisplayTypeName(UnrealMemberInfo member)
    {
        string typeName = member.TypeName;
        if (member.Address == 0)
        {
            return string.IsNullOrWhiteSpace(typeName) ? "Property" : typeName;
        }

        return ResolvePropertyTypeName(typeName, member.Address, depth: 0);
    }

    private UnrealMemberValue ReadValue(
        ulong address,
        string typeName,
        int size,
        ulong propertyAddress,
        int depth,
        UnrealValueReadOptions options)
    {
        if (address == 0 || size < 0)
        {
            return Unreadable(address, typeName, "Invalid value address or size.");
        }

        try
        {
            if (typeName.Contains("BoolProperty", StringComparison.Ordinal))
            {
                return ReadBoolValue(address, propertyAddress, typeName);
            }

            if (typeName.Contains("ByteProperty", StringComparison.Ordinal))
            {
                return ReadByteValue(address, propertyAddress, typeName);
            }

            if (typeName.Contains("EnumProperty", StringComparison.Ordinal))
            {
                return ReadEnumValue(address, propertyAddress, typeName);
            }

            if (typeName.Contains("Int8Property", StringComparison.Ordinal))
            {
                return TryReadByte(address, out byte value)
                    ? Value(
                        UnrealValueKind.Int8,
                        unchecked((sbyte)value).ToString(CultureInfo.InvariantCulture),
                        address,
                        "int8",
                        unchecked((sbyte)value))
                    : Unreadable(address, "int8", "Int8 read failed.");
            }

            if (typeName.Contains("Int16Property", StringComparison.Ordinal))
            {
                return TryReadInt16(address, out short value)
                    ? Value(
                        UnrealValueKind.Int16,
                        value.ToString(CultureInfo.InvariantCulture),
                        address,
                        "int16",
                        value)
                    : Unreadable(address, "int16", "Int16 read failed.");
            }

            if (typeName.Contains("UInt16Property", StringComparison.Ordinal))
            {
                return TryReadUInt16(address, out ushort value)
                    ? Value(
                        UnrealValueKind.UInt16,
                        value.ToString(CultureInfo.InvariantCulture),
                        address,
                        "uint16",
                        value)
                    : Unreadable(address, "uint16", "UInt16 read failed.");
            }

            if (typeName.Contains("Int64Property", StringComparison.Ordinal))
            {
                return TryReadInt64(address, out long value)
                    ? Value(
                        UnrealValueKind.Int64,
                        value.ToString(CultureInfo.InvariantCulture),
                        address,
                        "int64",
                        value)
                    : Unreadable(address, "int64", "Int64 read failed.");
            }

            if (typeName.Contains("UInt64Property", StringComparison.Ordinal))
            {
                return TryReadUInt64(address, out ulong value)
                    ? Value(
                        UnrealValueKind.UInt64,
                        value.ToString(CultureInfo.InvariantCulture),
                        address,
                        "uint64",
                        value)
                    : Unreadable(address, "uint64", "UInt64 read failed.");
            }

            if (typeName.Contains("IntProperty", StringComparison.Ordinal))
            {
                return TryReadInt32(address, out int value)
                    ? Value(
                        UnrealValueKind.Int32,
                        value.ToString(CultureInfo.InvariantCulture),
                        address,
                        "int32",
                        value)
                    : Unreadable(address, "int32", "Int32 read failed.");
            }

            if (typeName.Contains("UInt32Property", StringComparison.Ordinal))
            {
                return TryReadUInt32(address, out uint value)
                    ? Value(
                        UnrealValueKind.UInt32,
                        value.ToString(CultureInfo.InvariantCulture),
                        address,
                        "uint32",
                        value)
                    : Unreadable(address, "uint32", "UInt32 read failed.");
            }

            if (typeName.Contains("FloatProperty", StringComparison.Ordinal))
            {
                return TryReadSingle(address, out float value)
                    ? Value(
                        UnrealValueKind.Float,
                        FormatFloatingPoint(value),
                        address,
                        "float",
                        value)
                    : Unreadable(address, "float", "Float read failed.");
            }

            if (typeName.Contains("DoubleProperty", StringComparison.Ordinal))
            {
                return TryReadDouble(address, out double value)
                    ? Value(
                        UnrealValueKind.Double,
                        FormatFloatingPoint(value),
                        address,
                        "double",
                        value)
                    : Unreadable(address, "double", "Double read failed.");
            }

            if (typeName.Contains("NameProperty", StringComparison.Ordinal))
            {
                return _names.TryResolveName(address, out string name)
                    ? Value(UnrealValueKind.Name, name, address, "FName", name)
                    : Unreadable(address, "FName", "FName read failed.");
            }

            if (typeName.Contains("StrProperty", StringComparison.Ordinal))
            {
                return TryReadFString(address, options.MaxStringLength, out string text)
                    ? Value(UnrealValueKind.Text, text, address, "FString", text)
                    : Unreadable(address, "FString", "FString read failed.");
            }

            if (typeName.Contains("TextProperty", StringComparison.Ordinal))
            {
                return ReadTextValue(address, typeName);
            }

            if (typeName.Contains("WeakObjectProperty", StringComparison.Ordinal) ||
                typeName.Contains("LazyObjectProperty", StringComparison.Ordinal))
            {
                return ReadWeakObjectValue(address, typeName);
            }

            if (typeName.Contains("SoftObjectProperty", StringComparison.Ordinal) ||
                typeName.Contains("SoftClassProperty", StringComparison.Ordinal))
            {
                return ReadSoftObjectValue(address, typeName);
            }

            if (typeName.Contains("DelegateProperty", StringComparison.Ordinal))
            {
                return ReadDelegateValue(address, typeName);
            }

            if (typeName.Contains("Multicast", StringComparison.Ordinal) &&
                typeName.Contains("DelegateProperty", StringComparison.Ordinal))
            {
                return ReadMulticastDelegateValue(address, typeName);
            }

            if (typeName.Contains("ObjectProperty", StringComparison.Ordinal) ||
                typeName.Contains("ClassProperty", StringComparison.Ordinal) ||
                typeName.Contains("InterfaceProperty", StringComparison.Ordinal) ||
                typeName.Contains("ObjectPtrProperty", StringComparison.Ordinal) ||
                typeName.Contains("AssetObjectProperty", StringComparison.Ordinal))
            {
                return ReadObjectValue(address, typeName);
            }

            if (typeName.Contains("ArrayProperty", StringComparison.Ordinal))
            {
                return ReadArrayPreview(address, propertyAddress, typeName, depth, options);
            }

            if (typeName.Contains("SetProperty", StringComparison.Ordinal) ||
                typeName.Contains("MapProperty", StringComparison.Ordinal))
            {
                return ReadSetOrMapPreview(address, propertyAddress, typeName);
            }

            string displayType = ResolvePropertyTypeName(typeName, propertyAddress, depth);
            if (TryReadBuiltInStruct(address, displayType, size, out UnrealMemberValue builtIn))
            {
                return builtIn;
            }

            if (typeName.Contains("StructProperty", StringComparison.Ordinal))
            {
                return ReadStructValue(
                    address,
                    propertyAddress,
                    displayType,
                    depth,
                    options);
            }

            return ReadRawValue(address, size, displayType);
        }
        catch (Exception exception) when (
            exception is ArgumentOutOfRangeException or
            OverflowException or
            InvalidOperationException)
        {
            return Unreadable(address, typeName, exception.Message);
        }
    }

    private UnrealMemberValue ReadBoolValue(
        ulong address,
        ulong propertyAddress,
        string typeName)
    {
        bool hasBitField = false;
        byte bitIndex = 0;
        byte bitMask = 0;
        if (propertyAddress != 0 &&
            TryReadBytes(
                propertyAddress + (uint)_offsets.FPropertyBitMaskField,
                4,
                out byte[] maskBytes) &&
            maskBytes.Length == 4 &&
            maskBytes[2] != 0 &&
            maskBytes[3] > 0)
        {
            bitIndex = maskBytes[1];
            bitMask = maskBytes[2];
            hasBitField = true;
        }

        if (!hasBitField)
        {
            return TryReadByte(address, out byte value)
                ? Value(
                    UnrealValueKind.Bool,
                    value != 0 ? "true" : "false",
                    address,
                    "bool",
                    value != 0)
                : Unreadable(address, "bool", "Boolean read failed.");
        }

        if (!TryReadByte(address + bitIndex, out byte fieldValue))
        {
            return Unreadable(address, "bool", "Boolean bitfield read failed.");
        }

        bool result = (fieldValue & bitMask) != 0;
        return new UnrealMemberValue
        {
            Kind = UnrealValueKind.Bool,
            DisplayName = result ? "true" : "false",
            Value = result,
            Address = address + bitIndex,
            TypeName = "bool",
            Error = $"bit 0x{bitMask:X2} @+{bitIndex}"
        };
    }

    private UnrealMemberValue ReadByteValue(
        ulong address,
        ulong propertyAddress,
        string typeName)
    {
        if (!TryReadByte(address, out byte value))
        {
            return Unreadable(address, "uint8", "Byte read failed.");
        }

        if (TryReadPropertyObjectClass(
                propertyAddress,
                out ulong enumAddress,
                out string enumType) &&
            enumAddress != 0)
        {
            string enumValue = _resolveEnumValue(enumAddress, value) ?? string.Empty;
            string display = string.IsNullOrWhiteSpace(enumValue)
                ? value.ToString(CultureInfo.InvariantCulture)
                : $"{enumValue} ({value})";
            return Value(
                UnrealValueKind.Enum,
                display,
                address,
                $"TEnumAsByte<{enumType}>",
                value);
        }

        return Value(
            UnrealValueKind.UInt8,
            value.ToString(CultureInfo.InvariantCulture),
            address,
            "uint8",
            value);
    }

    private UnrealMemberValue ReadEnumValue(
        ulong address,
        ulong propertyAddress,
        string typeName)
    {
        int valueSize = 1;
        if (TryReadPropertyObjectClass(
                propertyAddress,
                out ulong underlyingProperty,
                out _) &&
            underlyingProperty != 0 &&
            TryReadInt32(
                underlyingProperty + (uint)_offsets.FPropertyElementSize,
                out int elementSize) &&
            elementSize is 1 or 2 or 4 or 8)
        {
            valueSize = elementSize;
        }

        long value;
        switch (valueSize)
        {
            case 1:
                if (!TryReadByte(address, out byte byteValue))
                {
                    return Unreadable(address, typeName, "Enum byte read failed.");
                }

                value = byteValue;
                break;
            case 2:
                if (!TryReadInt16(address, out short shortValue))
                {
                    return Unreadable(address, typeName, "Enum int16 read failed.");
                }

                value = shortValue;
                break;
            case 4:
                if (!TryReadInt32(address, out int intValue))
                {
                    return Unreadable(address, typeName, "Enum int32 read failed.");
                }

                value = intValue;
                break;
            default:
                if (!TryReadInt64(address, out value))
                {
                    return Unreadable(address, typeName, "Enum int64 read failed.");
                }

                break;
        }

        ulong enumAddress = 0;
        string enumType = string.Empty;
        if (propertyAddress != 0)
        {
            TryReadPropertyObject(
                propertyAddress + (uint)_offsets.FPropertyEnumField,
                out enumAddress,
                out enumType);
        }

        string enumValue = _resolveEnumValue(enumAddress, value) ?? string.Empty;
        string display = string.IsNullOrWhiteSpace(enumValue)
            ? value.ToString(CultureInfo.InvariantCulture)
            : $"{enumValue} ({value})";
        return Value(
            UnrealValueKind.Enum,
            display,
            address,
            string.IsNullOrWhiteSpace(enumType) ? "enum" : enumType,
            value);
    }

    private UnrealMemberValue ReadObjectValue(ulong address, string typeName)
    {
        if (!TryReadPointer(address, out ulong objectAddress) || objectAddress == 0)
        {
            return Value(
                UnrealValueKind.Object,
                "null",
                address,
                ResolveObjectTypeName(typeName),
                null);
        }

        string objectName = ResolveObjectDisplayName(objectAddress);
        return Value(
            UnrealValueKind.Object,
            string.IsNullOrWhiteSpace(objectName)
                ? $"0x{objectAddress:X}"
                : $"{objectName} 0x{objectAddress:X}",
            address,
            ResolveObjectTypeName(typeName),
            objectAddress);
    }

    private UnrealMemberValue ReadWeakObjectValue(ulong address, string typeName)
    {
        if (!TryReadInt32(address, out int objectIndex) ||
            !TryReadInt32(address + 4, out int serialNumber))
        {
            return Unreadable(address, typeName, "Weak object read failed.");
        }

        if (objectIndex < 0 || serialNumber == 0)
        {
            return Value(
                UnrealValueKind.Object,
                "null",
                address,
                ResolveObjectTypeName(typeName),
                null);
        }

        UnrealObjectInfo? target = _objects.Values.FirstOrDefault(
            item => item.Index == objectIndex);
        if (target is null)
        {
            return Value(
                UnrealValueKind.Object,
                $"<stale index {objectIndex}>",
                address,
                ResolveObjectTypeName(typeName),
                null);
        }

        return Value(
            UnrealValueKind.Object,
            $"{target.FullName} 0x{target.Address:X}",
            address,
            ResolveObjectTypeName(typeName),
            target.Address);
    }

    private UnrealMemberValue ReadSoftObjectValue(ulong address, string typeName)
    {
        string pathName = _names.TryResolveName(address + 16, out string resolved)
            ? resolved
            : string.Empty;
        string subPath = string.Empty;
        ulong subPathAddress = address + 16 + (uint)Math.Max(_offsets.PointerSize, 4);
        if (TryReadFString(subPathAddress, 256, out string resolvedSubPath))
        {
            subPath = resolvedSubPath;
        }

        string display = string.IsNullOrWhiteSpace(pathName) ? "null" : pathName;
        if (!string.IsNullOrWhiteSpace(subPath) &&
            !subPath.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            display = $"{display}.{subPath}";
        }

        return Value(
            UnrealValueKind.Object,
            display,
            address,
            ResolveObjectTypeName(typeName),
            display);
    }

    private UnrealMemberValue ReadDelegateValue(ulong address, string typeName)
    {
        if (!TryReadInt32(address, out int objectIndex) ||
            !TryReadInt32(address + 4, out int serialNumber) ||
            objectIndex < 0 ||
            serialNumber == 0)
        {
            return Value(
                UnrealValueKind.Object,
                "<unbound>",
                address,
                "Delegate",
                null);
        }

        UnrealObjectInfo? target = _objects.Values.FirstOrDefault(
            item => item.Index == objectIndex);
        string functionName = _names.TryResolveName(address + 8, out string resolved)
            ? resolved
            : string.Empty;
        string display = target is null
            ? functionName
            : $"{target.FullName}::{functionName}";
        return Value(
            UnrealValueKind.Object,
            string.IsNullOrWhiteSpace(display) ? "<unbound>" : display,
            address,
            "Delegate",
            target?.Address);
    }

    private UnrealMemberValue ReadMulticastDelegateValue(
        ulong address,
        string typeName)
    {
        int count = TryReadInt32(
            address + (uint)_offsets.PointerSize,
            out int value)
                ? Math.Max(value, 0)
                : 0;
        return new UnrealMemberValue
        {
            Kind = UnrealValueKind.Object,
            DisplayName = $"<multicast, {count} bound>",
            Value = count,
            Address = address,
            TypeName = "MulticastDelegate"
        };
    }

    private UnrealMemberValue ReadTextValue(ulong address, string typeName)
    {
        ulong textAddress = TryReadPointer(address, out ulong value) ? value : 0;
        return Value(
            UnrealValueKind.Text,
            textAddress == 0 ? "<FText empty>" : $"<FText @0x{textAddress:X}>",
            address,
            "FText",
            textAddress);
    }

    private UnrealMemberValue ReadArrayPreview(
        ulong address,
        ulong propertyAddress,
        string typeName,
        int depth,
        UnrealValueReadOptions options)
    {
        if (!TryReadPointer(address, out ulong data) ||
            !TryReadInt32(
                address + (uint)_offsets.PointerSize,
                out int count) ||
            count < 0 ||
            count > MaximumArrayCount)
        {
            return Unreadable(address, typeName, "TArray read failed.");
        }

        string elementType = "?";
        int elementSize = 0;
        if (TryReadPropertyObjectClass(
                propertyAddress,
                out ulong elementProperty,
                out _) &&
            elementProperty != 0)
        {
            elementType = ResolvePropertyTypeNameFromProperty(
                elementProperty,
                depth + 1);
            if (!TryReadInt32(
                    elementProperty + (uint)_offsets.FPropertyElementSize,
                    out elementSize))
            {
                elementSize = 0;
            }
        }

        int preview = Math.Clamp(options.ArrayPreview, 0, 64);
        List<UnrealMemberValue> children = [];
        if (data != 0 && elementSize > 0 && elementSize <= 1_048_576)
        {
            for (int index = 0; index < Math.Min(count, preview); index++)
            {
                UnrealMemberValue item = ReadValue(
                    data + (uint)(index * elementSize),
                    elementType,
                    elementSize,
                    0,
                    depth + 1,
                    options);
                item.DisplayName = $"[{index}] {item.DisplayName}";
                children.Add(item);
            }
        }

        return new UnrealMemberValue
        {
            Kind = UnrealValueKind.Array,
            DisplayName = $"TArray<{elementType}>[{count}]",
            Value = count,
            Address = data,
            TypeName = $"TArray<{elementType}>",
            Children = children
        };
    }

    private UnrealMemberValue ReadSetOrMapPreview(
        ulong address,
        ulong propertyAddress,
        string typeName)
    {
        int count = TryReadInt32(
            address + (uint)_offsets.PointerSize,
            out int value)
                ? Math.Max(value, 0)
                : 0;
        string displayType = ResolvePropertyTypeName(typeName, propertyAddress, 0);
        return new UnrealMemberValue
        {
            Kind = UnrealValueKind.Struct,
            DisplayName = $"{displayType}[{count}]",
            Value = count,
            Address = address,
            TypeName = displayType
        };
    }

    private UnrealMemberValue ReadStructValue(
        ulong address,
        ulong propertyAddress,
        string displayType,
        int depth,
        UnrealValueReadOptions options)
    {
        if (!TryReadPropertyObjectClass(
                propertyAddress,
                out ulong structAddress,
                out string structName))
        {
            return new UnrealMemberValue
            {
                Kind = UnrealValueKind.Struct,
                DisplayName = $"{displayType} @0x{address:X}",
                Address = address,
                TypeName = displayType
            };
        }

        if (depth >= options.StructDepth)
        {
            return new UnrealMemberValue
            {
                Kind = UnrealValueKind.Struct,
                DisplayName = $"F{structName} @0x{address:X}",
                Address = address,
                TypeName = $"F{structName}"
            };
        }

        UnrealObjectInfo? structObject = FindObject(structAddress) ??
            _objects.Values.FirstOrDefault(item =>
                string.Equals(item.Name, structName, StringComparison.Ordinal));
        if (structObject is null)
        {
            return new UnrealMemberValue
            {
                Kind = UnrealValueKind.Struct,
                DisplayName = $"F{structName} @0x{address:X}",
                Address = address,
                TypeName = $"F{structName}"
            };
        }

        IReadOnlyList<UnrealMemberInfo> members = _getMembers(
            structObject,
            false);
        UnrealObjectInfo valueObject = new()
        {
            Address = address,
            Name = structObject.Name,
            ClassAddress = structObject.Address,
            ClassName = structObject.Name,
            Kind = UnrealObjectKind.Struct
        };
        List<UnrealMemberValue> children = [];
        foreach (UnrealMemberInfo member in members.Take(MaximumStructMembers))
        {
            if (member.Offset < 0)
            {
                continue;
            }

            UnrealMemberValue value = ReadMemberValue(
                valueObject,
                member,
                depth + 1,
                options);
            value.DisplayName = $"{member.Name} = {value.DisplayName}";
            children.Add(value);
        }

        return new UnrealMemberValue
        {
            Kind = UnrealValueKind.Struct,
            DisplayName = $"F{structName} ({children.Count} members)",
            Value = children.Count,
            Address = address,
            TypeName = $"F{structName}",
            Children = children
        };
    }

    private bool TryReadBuiltInStruct(
        ulong address,
        string displayType,
        int size,
        out UnrealMemberValue value)
    {
        value = Unreadable(address, displayType, "Unknown struct.");
        string simpleName = displayType.TrimStart('F');
        if (simpleName.Equals("Vector", StringComparison.Ordinal) ||
            simpleName.Equals("Vector3f", StringComparison.Ordinal))
        {
            return TryReadFloatingVector(
                address,
                ["X", "Y", "Z"],
                size >= 3 * 8 ? 8 : 4,
                out value);
        }

        if (simpleName.Equals("Vector2D", StringComparison.Ordinal) ||
            simpleName.Equals("Vector2f", StringComparison.Ordinal))
        {
            return TryReadFloatingVector(
                address,
                ["X", "Y"],
                size >= 2 * 8 ? 8 : 4,
                out value);
        }

        if (simpleName.Equals("Vector4", StringComparison.Ordinal) ||
            simpleName.Equals("Vector4f", StringComparison.Ordinal) ||
            simpleName.Equals("Plane", StringComparison.Ordinal))
        {
            return TryReadFloatingVector(
                address,
                ["X", "Y", "Z", "W"],
                size >= 4 * 8 ? 8 : 4,
                out value);
        }

        if (simpleName.Equals("Rotator", StringComparison.Ordinal))
        {
            return TryReadFloatingVector(
                address,
                ["Pitch", "Yaw", "Roll"],
                size >= 3 * 8 ? 8 : 4,
                out value);
        }

        if (simpleName.Equals("Quat", StringComparison.Ordinal))
        {
            return TryReadFloatingVector(
                address,
                ["X", "Y", "Z", "W"],
                size >= 4 * 8 ? 8 : 4,
                out value);
        }

        if (simpleName.Equals("LinearColor", StringComparison.Ordinal))
        {
            return TryReadFloatingVector(
                address,
                ["R", "G", "B", "A"],
                4,
                out value);
        }

        if (simpleName.Equals("IntPoint", StringComparison.Ordinal))
        {
            return TryReadIntegerVector(address, ["X", "Y"], out value);
        }

        if (simpleName.Equals("IntVector", StringComparison.Ordinal))
        {
            return TryReadIntegerVector(address, ["X", "Y", "Z"], out value);
        }

        if (simpleName.Equals("IntVector4", StringComparison.Ordinal))
        {
            return TryReadIntegerVector(address, ["X", "Y", "Z", "W"], out value);
        }

        if (simpleName.Equals("Color", StringComparison.Ordinal) && size >= 4)
        {
            if (!TryReadBytes(address, 4, out byte[] bytes))
            {
                return false;
            }

            value = new UnrealMemberValue
            {
                Kind = UnrealValueKind.Struct,
                DisplayName = $"R={bytes[2]} G={bytes[1]} B={bytes[0]} A={bytes[3]}",
                Value = bytes,
                Address = address,
                TypeName = "FColor",
                Children =
                [
                    ScalarChild("B", "uint8", bytes[0], address),
                    ScalarChild("G", "uint8", bytes[1], address + 1),
                    ScalarChild("R", "uint8", bytes[2], address + 2),
                    ScalarChild("A", "uint8", bytes[3], address + 3)
                ]
            };
            return true;
        }

        if (simpleName.Equals("Guid", StringComparison.Ordinal) && size >= 16)
        {
            uint[] parts = new uint[4];
            for (int index = 0; index < parts.Length; index++)
            {
                if (!TryReadUInt32(address + (uint)(index * 4), out parts[index]))
                {
                    return false;
                }
            }

            value = new UnrealMemberValue
            {
                Kind = UnrealValueKind.Struct,
                DisplayName = string.Join("-", parts.Select(part => part.ToString("X8"))),
                Value = parts,
                Address = address,
                TypeName = "FGuid",
                Children = parts
                    .Select((part, index) => ScalarChild(
                        ((char)('A' + index)).ToString(),
                        "uint32",
                        part,
                        address + (uint)(index * 4)))
                    .ToArray()
            };
            return true;
        }

        if (simpleName.Equals("DateTime", StringComparison.Ordinal) ||
            simpleName.Equals("Timespan", StringComparison.Ordinal))
        {
            if (!TryReadInt64(address, out long ticks))
            {
                return false;
            }

            string display;
            try
            {
                display = simpleName.Equals("DateTime", StringComparison.Ordinal)
                    ? DateTime.FromBinary(ticks)
                        .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    : TimeSpan.FromTicks(ticks).ToString("c", CultureInfo.InvariantCulture);
            }
            catch (ArgumentException)
            {
                display = ticks.ToString(CultureInfo.InvariantCulture);
            }

            value = new UnrealMemberValue
            {
                Kind = UnrealValueKind.Struct,
                DisplayName = display,
                Value = ticks,
                Address = address,
                TypeName = $"F{simpleName}",
                Children =
                [
                    ScalarChild("Ticks", "int64", ticks, address)
                ]
            };
            return true;
        }

        if (simpleName.Equals("Transform", StringComparison.Ordinal))
        {
            int componentSize = size >= 96 ? 8 : 4;
            int vectorSize = componentSize * 4;
            if (!TryReadFloatingVector(
                    address,
                    ["X", "Y", "Z", "W"],
                    componentSize,
                    out UnrealMemberValue rotation) ||
                !TryReadFloatingVector(
                    address + (uint)vectorSize,
                    ["X", "Y", "Z"],
                    componentSize,
                    out UnrealMemberValue translation) ||
                !TryReadFloatingVector(
                    address + (uint)(vectorSize * 2),
                    ["X", "Y", "Z"],
                    componentSize,
                    out UnrealMemberValue scale))
            {
                return false;
            }

            rotation.DisplayName = $"Rotation: {rotation.DisplayName}";
            translation.DisplayName = $"Translation: {translation.DisplayName}";
            scale.DisplayName = $"Scale3D: {scale.DisplayName}";
            value = new UnrealMemberValue
            {
                Kind = UnrealValueKind.Struct,
                DisplayName = $"{rotation.DisplayName}; {translation.DisplayName}; {scale.DisplayName}",
                Address = address,
                TypeName = "FTransform",
                Children = [rotation, translation, scale]
            };
            return true;
        }

        return false;
    }

    private bool TryReadFloatingVector(
        ulong address,
        IReadOnlyList<string> names,
        int componentSize,
        out UnrealMemberValue value)
    {
        bool doublePrecision = componentSize >= 8;
        int elementSize = doublePrecision ? 8 : 4;
        List<UnrealMemberValue> children = new(names.Count);
        List<string> text = new(names.Count);
        for (int index = 0; index < names.Count; index++)
        {
            ulong componentAddress = address + (uint)(index * elementSize);
            double component;
            if (doublePrecision)
            {
                if (!TryReadDouble(componentAddress, out component))
                {
                    value = Unreadable(address, "Struct", "Double vector read failed.");
                    return false;
                }
            }
            else
            {
                if (!TryReadSingle(componentAddress, out float componentValue))
                {
                    value = Unreadable(address, "Struct", "Float vector read failed.");
                    return false;
                }

                component = componentValue;
            }

            string typeName = doublePrecision ? "double" : "float";
            children.Add(new UnrealMemberValue
            {
                Kind = doublePrecision ? UnrealValueKind.Double : UnrealValueKind.Float,
                DisplayName = names[index],
                Value = component,
                Address = componentAddress,
                TypeName = typeName
            });
            text.Add($"{names[index]}={FormatFloatingPoint(component)}");
        }

        value = new UnrealMemberValue
        {
            Kind = UnrealValueKind.Struct,
            DisplayName = string.Join(" ", text),
            Address = address,
            TypeName = doublePrecision ? "DoubleStruct" : "FloatStruct",
            Children = children
        };
        return true;
    }

    private bool TryReadIntegerVector(
        ulong address,
        IReadOnlyList<string> names,
        out UnrealMemberValue value)
    {
        List<UnrealMemberValue> children = new(names.Count);
        List<string> text = new(names.Count);
        for (int index = 0; index < names.Count; index++)
        {
            ulong componentAddress = address + (uint)(index * 4);
            if (!TryReadInt32(componentAddress, out int component))
            {
                value = Unreadable(address, "Struct", "Integer vector read failed.");
                return false;
            }

            children.Add(new UnrealMemberValue
            {
                Kind = UnrealValueKind.Int32,
                DisplayName = names[index],
                Value = component,
                Address = componentAddress,
                TypeName = "int32"
            });
            text.Add($"{names[index]}={component}");
        }

        value = new UnrealMemberValue
        {
            Kind = UnrealValueKind.Struct,
            DisplayName = string.Join(" ", text),
            Address = address,
            TypeName = "IntStruct",
            Children = children
        };
        return true;
    }

    private UnrealMemberValue ReadRawValue(ulong address, int size, string typeName)
    {
        int byteCount = Math.Clamp(size <= 0 ? 16 : size, 1, 16);
        if (!TryReadBytes(address, byteCount, out byte[] bytes))
        {
            return Unreadable(address, typeName, "Raw value read failed.");
        }

        string hex = Convert.ToHexString(bytes);
        string display = size <= 16 ? hex : $"{hex}... ({size} bytes)";
        return Value(
            UnrealValueKind.Unknown,
            display,
            address,
            typeName,
            bytes);
    }

    private string ResolvePropertyTypeName(
        string propertyType,
        ulong propertyAddress,
        int depth)
    {
        if (depth > 4 || string.IsNullOrWhiteSpace(propertyType))
        {
            return string.IsNullOrWhiteSpace(propertyType) ? "Property" : propertyType;
        }

        bool hasObjectClass = TryReadPropertyObjectClass(
            propertyAddress,
            out ulong objectClass,
            out string objectClassName);
        bool hasEnumType = TryReadPropertyObject(
            propertyAddress + (uint)_offsets.FPropertyEnumField,
            out ulong enumAddress,
            out string enumTypeName);

        if (propertyType.Contains("ArrayProperty", StringComparison.Ordinal) &&
            hasObjectClass &&
            objectClass != 0)
        {
            return $"TArray<{ResolvePropertyTypeNameFromProperty(objectClass, depth + 1)}>";
        }

        if (propertyType.Contains("SetProperty", StringComparison.Ordinal) &&
            hasObjectClass &&
            objectClass != 0)
        {
            return $"TSet<{ResolvePropertyTypeNameFromProperty(objectClass, depth + 1)}>";
        }

        if (propertyType.Contains("MapProperty", StringComparison.Ordinal) &&
            hasObjectClass &&
            objectClass != 0)
        {
            string valueType = hasEnumType && enumAddress != 0
                ? ResolvePropertyTypeNameFromProperty(enumAddress, depth + 1)
                : "?";
            return $"TMap<{ResolvePropertyTypeNameFromProperty(objectClass, depth + 1)}, {valueType}>";
        }

        if (propertyType.Contains("OptionalProperty", StringComparison.Ordinal) &&
            hasObjectClass &&
            objectClass != 0)
        {
            return $"TOptional<{ResolvePropertyTypeNameFromProperty(objectClass, depth + 1)}>";
        }

        if (propertyType.Contains("EnumProperty", StringComparison.Ordinal) &&
            hasEnumType &&
            enumAddress != 0)
        {
            return string.IsNullOrWhiteSpace(enumTypeName) ? "enum" : enumTypeName;
        }

        if (propertyType.Contains("ByteProperty", StringComparison.Ordinal) &&
            hasObjectClass &&
            objectClass != 0 &&
            !string.IsNullOrWhiteSpace(objectClassName))
        {
            return $"TEnumAsByte<{objectClassName}>";
        }

        if (propertyType.Contains("StructProperty", StringComparison.Ordinal) &&
            hasObjectClass &&
            objectClass != 0)
        {
            return string.IsNullOrWhiteSpace(objectClassName)
                ? "Struct"
                : objectClassName;
        }

        if ((propertyType.Contains("ObjectProperty", StringComparison.Ordinal) ||
             propertyType.Contains("ClassProperty", StringComparison.Ordinal) ||
             propertyType.Contains("InterfaceProperty", StringComparison.Ordinal)) &&
            hasObjectClass &&
            objectClass != 0)
        {
            return string.IsNullOrWhiteSpace(objectClassName)
                ? ResolveObjectTypeName(propertyType)
                : objectClassName;
        }

        return propertyType switch
        {
            "BoolProperty" => "bool",
            "ByteProperty" => "uint8",
            "Int8Property" => "int8",
            "Int16Property" => "int16",
            "UInt16Property" => "uint16",
            "IntProperty" => "int32",
            "UInt32Property" => "uint32",
            "Int64Property" => "int64",
            "UInt64Property" => "uint64",
            "FloatProperty" => "float",
            "DoubleProperty" => "double",
            "NameProperty" => "FName",
            "StrProperty" => "FString",
            "TextProperty" => "FText",
            _ => propertyType
        };
    }

    private string ResolvePropertyTypeNameFromProperty(
        ulong propertyAddress,
        int depth)
    {
        if (propertyAddress == 0)
        {
            return "?";
        }

        string fieldClassName = TryResolveFieldClassName(
            propertyAddress,
            out string resolvedClassName)
                ? resolvedClassName
                : "Property";
        return ResolvePropertyTypeName(fieldClassName, propertyAddress, depth);
    }

    private string ResolveObjectTypeName(string propertyType)
    {
        if (propertyType.Contains("ClassProperty", StringComparison.Ordinal))
        {
            return "UClass*";
        }

        if (propertyType.Contains("InterfaceProperty", StringComparison.Ordinal))
        {
            return "UInterface*";
        }

        if (propertyType.Contains("SoftClassProperty", StringComparison.Ordinal))
        {
            return "FSoftClassPath";
        }

        if (propertyType.Contains("SoftObjectProperty", StringComparison.Ordinal))
        {
            return "FSoftObjectPath";
        }

        return "UObject*";
    }

    private bool TryReadPropertyObjectClass(
        ulong propertyAddress,
        out ulong objectAddress,
        out string objectName) =>
        TryReadPropertyObject(
            propertyAddress == 0
                ? 0
                : propertyAddress + (uint)_offsets.FPropertyObjectClassType,
            out objectAddress,
            out objectName);

    private bool TryReadPropertyObject(
        ulong pointerAddress,
        out ulong objectAddress,
        out string objectName)
    {
        objectAddress = 0;
        objectName = string.Empty;
        if (pointerAddress == 0 ||
            !TryReadPointer(pointerAddress, out objectAddress) ||
            objectAddress == 0)
        {
            objectAddress = 0;
            return false;
        }

        objectName = ResolveObjectName(objectAddress);
        return !string.IsNullOrWhiteSpace(objectName);
    }

    private bool TryResolveFieldClassName(
        ulong fieldAddress,
        out string className)
    {
        className = string.Empty;
        if (fieldAddress == 0 ||
            !TryReadPointer(
                fieldAddress + (uint)_offsets.FFieldClass,
                out ulong fieldClass) ||
            fieldClass == 0)
        {
            return false;
        }

        if (_offsets.FFieldClassName > 0 &&
            _names.TryResolveName(
                fieldClass + (uint)_offsets.FFieldClassName,
                out className) &&
            !string.IsNullOrWhiteSpace(className))
        {
            return true;
        }

        className = ResolveObjectName(fieldClass);
        return !string.IsNullOrWhiteSpace(className);
    }

    private bool TryReadFString(
        ulong address,
        int maxLength,
        out string value)
    {
        value = string.Empty;
        if (!TryReadPointer(address, out ulong data) ||
            data == 0 ||
            !TryReadInt32(address + (uint)_offsets.PointerSize, out int length) ||
            length <= 1 ||
            length > 1_048_576)
        {
            return false;
        }

        int characterCount = length - 1;
        int readCount = maxLength <= 0
            ? 0
            : Math.Min(characterCount, maxLength);
        if (readCount == 0)
        {
            value = string.Empty;
            return true;
        }

        byte[] bytes = new byte[checked(readCount * 2)];
        if (!_process.TryReadBytes(data, bytes))
        {
            return false;
        }

        value = Encoding.Unicode.GetString(bytes);
        if (readCount < characterCount)
        {
            value = $"{value}... ({characterCount} chars)";
        }

        return true;
    }

    private string ResolveObjectDisplayName(ulong address)
    {
        UnrealObjectInfo? objectInfo = FindObject(address);
        if (objectInfo is not null)
        {
            return $"{objectInfo.FullName} ({objectInfo.ClassName})";
        }

        string name = ResolveObjectName(address);
        return string.IsNullOrWhiteSpace(name) ? string.Empty : name;
    }

    private string ResolveObjectName(ulong address)
    {
        if (_objects.TryGetValue(address, out UnrealObjectInfo? objectInfo))
        {
            return objectInfo.Name;
        }

        return _names.TryResolveName(
                   address + (uint)_offsets.UObjectName,
                   out string name)
            ? name
            : string.Empty;
    }

    private UnrealObjectInfo? FindObject(ulong address)
    {
        if (address == 0)
        {
            return null;
        }

        return _objects.TryGetValue(address, out UnrealObjectInfo? objectInfo)
            ? objectInfo
            : null;
    }

    private static UnrealMemberValue ScalarChild(
        string name,
        string typeName,
        object value,
        ulong address) => new()
    {
        Kind = typeName switch
        {
            "uint8" => UnrealValueKind.UInt8,
            "uint32" => UnrealValueKind.UInt32,
            "int64" => UnrealValueKind.Int64,
            _ => UnrealValueKind.Unknown
        },
        DisplayName = name,
        Value = value,
        Address = address,
        TypeName = typeName
    };

    private static UnrealMemberValue Value(
        UnrealValueKind kind,
        string text,
        ulong address,
        string typeName,
        object? value) => new()
    {
        Kind = kind,
        DisplayName = text,
        Value = value,
        Address = address,
        TypeName = typeName
    };

    private static UnrealMemberValue Unreadable(
        ulong address,
        string typeName,
        string error) => new()
    {
        Kind = UnrealValueKind.Unreadable,
        DisplayName = "<unreadable>",
        Address = address,
        TypeName = typeName,
        Error = error
    };

    private static string FormatFloatingPoint(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        return Math.Abs(value) < 1_000_000_000
            ? value.ToString("0.####", CultureInfo.InvariantCulture)
            : value.ToString("G6", CultureInfo.InvariantCulture);
    }

    private bool TryReadByte(ulong address, out byte value)
    {
        value = 0;
        Span<byte> bytes = stackalloc byte[1];
        if (!_process.TryReadBytes(address, bytes))
        {
            return false;
        }

        value = bytes[0];
        return true;
    }

    private bool TryReadBytes(ulong address, int count, out byte[] value)
    {
        value = new byte[Math.Max(count, 0)];
        return value.Length == 0 || _process.TryReadBytes(address, value);
    }

    private bool TryReadInt16(ulong address, out short value)
    {
        value = 0;
        Span<byte> bytes = stackalloc byte[2];
        if (!_process.TryReadBytes(address, bytes))
        {
            return false;
        }

        value = BinaryPrimitives.ReadInt16LittleEndian(bytes);
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

    private bool TryReadInt64(ulong address, out long value)
    {
        value = 0;
        Span<byte> bytes = stackalloc byte[8];
        if (!_process.TryReadBytes(address, bytes))
        {
            return false;
        }

        value = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        return true;
    }

    private bool TryReadUInt64(ulong address, out ulong value)
    {
        value = 0;
        Span<byte> bytes = stackalloc byte[8];
        if (!_process.TryReadBytes(address, bytes))
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        return true;
    }

    private bool TryReadSingle(ulong address, out float value)
    {
        value = 0;
        Span<byte> bytes = stackalloc byte[4];
        if (!_process.TryReadBytes(address, bytes))
        {
            return false;
        }

        value = BinaryPrimitives.ReadSingleLittleEndian(bytes);
        return true;
    }

    private bool TryReadDouble(ulong address, out double value)
    {
        value = 0;
        Span<byte> bytes = stackalloc byte[8];
        if (!_process.TryReadBytes(address, bytes))
        {
            return false;
        }

        value = BinaryPrimitives.ReadDoubleLittleEndian(bytes);
        return true;
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
}
