namespace DogeDebugger.Plugins.CEMono.Protocol;

public enum MonoDataCollectorCommand : byte
{
    InitializeRuntime = 0,
    GetObjectClass = 1,
    EnumerateDomains = 2,
    SetCurrentDomain = 3,
    EnumerateAssemblies = 4,
    GetImageFromAssembly = 5,
    GetImageName = 6,
    EnumerateClassesInImage = 7,
    EnumerateFieldsInClass = 8,
    EnumerateMethodsInClass = 9,
    CompileMethod = 10,
    GetMethodHeader = 11,
    GetMethodHeaderCode = 12,
    LookupRva = 13,
    GetJitInfo = 14,
    FindClass = 15,
    FindMethod = 16,
    GetMethodName = 17,
    GetMethodClass = 18,
    GetClassName = 19,
    GetClassNamespace = 20,
    FreeMethod = 21,
    Terminate = 22,
    DisassembleMethod = 23,
    GetMethodSignature = 24,
    GetParentClass = 25,
    GetStaticFieldAddressFromClass = 26,
    GetFieldClass = 27,
    GetArrayElementClass = 28,
    FindMethodByDescription = 29,
    InvokeMethod = 30,
    LoadAssembly = 31,
    GetFullTypeName = 32,
    CreateObject = 33,
    InitializeObject = 34,
    GetVTableFromClass = 35,
    GetMethodParameters = 36,
    IsClassGeneric = 37,
    IsIl2Cpp = 38,
    FillOptionalFunctionList = 39,
    GetStaticFieldValue = 40,
    SetStaticFieldValue = 41,
    GetClassImage = 42,
    FreeObject = 43,
    GetImageFileName = 44,
    GetClassNestingType = 45,
    SetLimitedConnection = 46,
    GetDataCollectorVersion = 47,
    CreateString = 48,
    EnumerateImages = 49,
    EnumerateClassesInImageEx = 50,
    IsClassEnum = 51,
    IsClassValueType = 52,
    IsClassSubclassOf = 53,
    GetArrayElementSize = 54,
    GetClassType = 55,
    GetClassOfType = 56,
    GetTypeOfMonoType = 57,
    GetReflectionTypeOfClassType = 58,
    GetReflectionMethodOfMonoMethod = 59,
    UnboxMonoObject = 60,
    CreateArray = 61,
    EnumerateInterfacesOfClass = 62,
    GetMethodFullName = 63,
    IsTypeByReference = 64,
    GetPointerTypeClass = 65,
    GetFieldType = 66,
    GetPointerTypeOfType = 67,
    EnumerateClassNestedTypes = 68,
    CollectGarbage = 69,
    GetMethodFlags = 70,
    DumpImage = 77
}

public enum MonoTypeCode : byte
{
    End = 0,
    Void = 1,
    Boolean = 2,
    Char = 3,
    I1 = 4,
    U1 = 5,
    I2 = 6,
    U2 = 7,
    I4 = 8,
    U4 = 9,
    I8 = 10,
    U8 = 11,
    R4 = 12,
    R8 = 13,
    String = 14,
    Pointer = 15,
    ByReference = 16,
    ValueType = 17,
    Class = 18,
    Var = 19,
    Array = 20,
    GenericInstance = 21,
    TypedByReference = 22,
    IntPtr = 24,
    UIntPtr = 25,
    FunctionPointer = 27,
    Object = 28,
    SingleDimensionalArray = 29,
    MethodVariable = 30,
    RequiredModifier = 31,
    OptionalModifier = 32,
    Internal = 33,
    Modifier = 64,
    Sentinel = 65,
    Pinned = 69,
    Enum = 85
}

[Flags]
public enum MonoFieldAttributes : ushort
{
    FieldAccessMask = 0x0007,
    CompilerControlled = 0x0000,
    Private = 0x0001,
    FamilyAndAssembly = 0x0002,
    Assembly = 0x0003,
    Family = 0x0004,
    FamilyOrAssembly = 0x0005,
    Public = 0x0006,
    Static = 0x0010,
    InitOnly = 0x0020,
    Literal = 0x0040,
    NotSerialized = 0x0080,
    HasFieldRva = 0x0100,
    SpecialName = 0x0200,
    RuntimeSpecialName = 0x0400,
    HasFieldMarshal = 0x1000,
    PInvokeImpl = 0x2000,
    HasDefault = 0x8000
}

[Flags]
public enum MonoMethodAttributes : ushort
{
    MemberAccessMask = 0x0007,
    CompilerControlled = 0x0000,
    Private = 0x0001,
    FamilyAndAssembly = 0x0002,
    Assembly = 0x0003,
    Family = 0x0004,
    FamilyOrAssembly = 0x0005,
    Public = 0x0006,
    UnmanagedExport = 0x0008,
    Static = 0x0010,
    Final = 0x0020,
    Virtual = 0x0040,
    HideBySignature = 0x0080,
    NewSlot = 0x0100,
    Strict = 0x0200,
    Abstract = 0x0400,
    SpecialName = 0x0800,
    PInvokeImpl = 0x2000
}

public sealed record MonoImageInfo(ulong Handle, string Name);

public sealed record MonoClassInfo(
    ulong Handle,
    ulong ParentHandle,
    ulong NestingTypeHandle,
    string Name,
    string Namespace,
    string FullName);

public sealed record MonoFieldInfo(
    ulong Handle,
    ulong TypeHandle,
    MonoTypeCode TypeCode,
    ulong DeclaringClassHandle,
    int Offset,
    MonoFieldAttributes Attributes,
    string Name,
    string TypeName)
{
    public bool IsStatic =>
        (Attributes & (MonoFieldAttributes.Static | MonoFieldAttributes.HasFieldRva)) != 0;

    public bool IsLiteral =>
        (Attributes & MonoFieldAttributes.Literal) != 0;
}

public sealed record MonoMethodInfo(
    ulong Handle,
    string Name,
    MonoMethodAttributes Attributes);

public sealed record MonoMethodSignature(
    string ReturnType,
    IReadOnlyList<string> ParameterTypes,
    IReadOnlyList<string> ParameterNames);

public sealed record MonoJitInfo(
    ulong JitInfo,
    ulong MethodHandle,
    ulong CodeStart,
    uint CodeSize);

public sealed record MonoImageDump(
    string ImageName,
    byte[] Data);
