using System.Text;
using System.IO;

namespace DogeDebugger.Plugins.CEMono.Protocol;

public sealed class MonoDataCollectorClient : IDisposable
{
    public const uint ExpectedProtocolVersion = 25042026;

    private readonly int _processId;
    private readonly object _commandSync = new();
    private MonoPipeClient? _pipe;
    private bool _disposed;

    public MonoDataCollectorClient(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        _processId = processId;
    }

    public int ProcessId =>
        _processId;

    public bool IsConnected =>
        !_disposed && _pipe?.IsConnected == true;

    public bool IsIl2Cpp { get; private set; }

    public uint ProtocolVersion { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken = default) =>
        Task.Run(ConnectCore, cancellationToken);

    public IReadOnlyList<MonoImageInfo> GetImages()
    {
        lock (_commandSync)
        {
            MonoPipeClient pipe = RequirePipe();
            List<ulong> assemblies = EnumerateAssemblyHandles(pipe);
            List<MonoImageInfo> images = new(assemblies.Count);
            foreach (ulong assembly in assemblies)
            {
                pipe.SendCommand(MonoDataCollectorCommand.GetImageFromAssembly);
                pipe.WriteQWord(assembly);
                ulong image = pipe.ReadQWord();
                if (image == 0)
                {
                    continue;
                }

                pipe.SendCommand(MonoDataCollectorCommand.GetImageName);
                pipe.WriteQWord(image);
                string name = pipe.ReadString();
                images.Add(new MonoImageInfo(image, name));
            }

            images.Sort(static (left, right) =>
                StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
            return images;
        }
    }

    public IReadOnlyList<MonoClassInfo> GetClasses(ulong imageHandle)
    {
        if (imageHandle == 0)
        {
            return [];
        }

        lock (_commandSync)
        {
            MonoPipeClient pipe = RequirePipe();
            pipe.SendCommand(MonoDataCollectorCommand.EnumerateClassesInImageEx);
            pipe.WriteQWord(imageHandle);
            int length = CheckedLength(pipe.ReadDWord(), "class table");
            if (length == 0)
            {
                return GetClassesLegacy(pipe, imageHandle);
            }

            ProtocolReader reader = new(pipe.ReadBytes(length));
            List<MonoClassInfo> classes = [];
            uint count = reader.ReadUInt32("class count");
            classes.Capacity = CheckedCapacity(count, "class count");
            for (uint index = 0; index < count; index++)
            {
                ulong handle = reader.ReadUInt64("class handle");
                ulong parent = reader.ReadUInt64("class parent");
                ulong nestingType = reader.ReadUInt64("class nesting type");
                string name = reader.ReadWordString("class name");
                string nameSpace = reader.ReadWordString("class namespace");
                string fullName = reader.ReadWordString("class full name");
                if (handle != 0)
                {
                    classes.Add(new MonoClassInfo(
                        handle,
                        parent,
                        nestingType,
                        name,
                        nameSpace,
                        fullName));
                }
            }

            return classes;
        }
    }

    public IReadOnlyList<MonoFieldInfo> GetFields(
        ulong classHandle,
        bool includeInherited = false)
    {
        if (classHandle == 0)
        {
            return [];
        }

        lock (_commandSync)
        {
            MonoPipeClient pipe = RequirePipe();
            List<MonoFieldInfo> fields = [];
            CollectFields(pipe, classHandle, fields);
            if (!includeInherited)
            {
                return fields;
            }

            HashSet<ulong> visited = [classHandle];
            ulong parent = GetParentClassCore(pipe, classHandle);
            while (parent != 0 && visited.Add(parent))
            {
                CollectFields(pipe, parent, fields);
                parent = GetParentClassCore(pipe, parent);
            }

            return fields;
        }
    }

    public IReadOnlyList<MonoMethodInfo> GetMethods(
        ulong classHandle,
        bool includeInherited = false)
    {
        if (classHandle == 0)
        {
            return [];
        }

        lock (_commandSync)
        {
            MonoPipeClient pipe = RequirePipe();
            List<MonoMethodInfo> methods = [];
            CollectMethods(pipe, classHandle, methods);
            if (!includeInherited)
            {
                methods.Sort(static (left, right) =>
                    StringComparer.Ordinal.Compare(left.Name, right.Name));
                return methods;
            }

            HashSet<ulong> visited = [classHandle];
            ulong parent = GetParentClassCore(pipe, classHandle);
            while (parent != 0 && visited.Add(parent))
            {
                CollectMethods(pipe, parent, methods);
                parent = GetParentClassCore(pipe, parent);
            }

            methods.Sort(static (left, right) =>
                StringComparer.Ordinal.Compare(left.Name, right.Name));
            return methods;
        }
    }

    public MonoMethodSignature? GetMethodSignature(ulong methodHandle)
    {
        if (methodHandle == 0)
        {
            return null;
        }

        lock (_commandSync)
        {
            MonoPipeClient pipe = RequirePipe();
            pipe.SendCommand(MonoDataCollectorCommand.GetMethodSignature);
            pipe.WriteQWord(methodHandle);
            int parameterCount = pipe.ReadByte();
            string[] parameterNames = new string[parameterCount];
            for (int index = 0; index < parameterCount; index++)
            {
                parameterNames[index] = pipe.ReadByteLengthString();
            }

            string returnType;
            string[] parameterTypes;
            if (IsIl2Cpp)
            {
                parameterTypes = new string[parameterCount];
                for (int index = 0; index < parameterCount; index++)
                {
                    parameterTypes[index] = pipe.ReadString();
                }

                returnType = pipe.ReadByteLengthString();
            }
            else
            {
                string types = pipe.ReadString();
                parameterTypes = types.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries);
                returnType = pipe.ReadByteLengthString();
            }

            return new MonoMethodSignature(
                returnType,
                parameterTypes,
                parameterNames);
        }
    }

    public ulong CompileMethod(ulong methodHandle)
    {
        if (methodHandle == 0)
        {
            return 0;
        }

        lock (_commandSync)
        {
            MonoPipeClient pipe = RequirePipe();
            pipe.SendCommand(MonoDataCollectorCommand.CompileMethod);
            pipe.WriteQWord(methodHandle);
            return pipe.ReadQWord();
        }
    }

    public string? DisassembleMethod(ulong methodHandle)
    {
        if (methodHandle == 0 || IsIl2Cpp)
        {
            return null;
        }

        lock (_commandSync)
        {
            MonoPipeClient pipe = RequirePipe();
            pipe.SendCommand(MonoDataCollectorCommand.DisassembleMethod);
            pipe.WriteQWord(methodHandle);
            return pipe.ReadString();
        }
    }

    public ulong GetParentClass(ulong classHandle)
    {
        if (classHandle == 0)
        {
            return 0;
        }

        lock (_commandSync)
        {
            return GetParentClassCore(RequirePipe(), classHandle);
        }
    }

    public string? GetClassName(ulong classHandle)
    {
        if (classHandle == 0)
        {
            return null;
        }

        lock (_commandSync)
        {
            MonoPipeClient pipe = RequirePipe();
            pipe.SendCommand(MonoDataCollectorCommand.GetClassName);
            pipe.WriteQWord(classHandle);
            return pipe.ReadString();
        }
    }

    public string? GetClassNamespace(ulong classHandle)
    {
        if (classHandle == 0)
        {
            return null;
        }

        lock (_commandSync)
        {
            MonoPipeClient pipe = RequirePipe();
            pipe.SendCommand(MonoDataCollectorCommand.GetClassNamespace);
            pipe.WriteQWord(classHandle);
            return pipe.ReadString();
        }
    }

    public string? GetFullTypeName(
        ulong classHandle,
        bool includeNamespace = true,
        int nameFormat = 1)
    {
        if (classHandle == 0)
        {
            return null;
        }

        lock (_commandSync)
        {
            MonoPipeClient pipe = RequirePipe();
            pipe.SendCommand(MonoDataCollectorCommand.GetFullTypeName);
            pipe.WriteQWord(classHandle);
            pipe.WriteByte(includeNamespace ? (byte)1 : (byte)0);
            pipe.WriteDWord(checked((uint)nameFormat));
            return pipe.ReadString();
        }
    }

    public ulong GetVTable(ulong domainHandle, ulong classHandle)
    {
        if (classHandle == 0)
        {
            return 0;
        }

        lock (_commandSync)
        {
            MonoPipeClient pipe = RequirePipe();
            pipe.SendCommand(MonoDataCollectorCommand.GetVTableFromClass);
            pipe.WriteQWord(domainHandle);
            pipe.WriteQWord(classHandle);
            return pipe.ReadQWord();
        }
    }

    public ulong GetStaticFieldAddress(ulong domainHandle, ulong classHandle)
    {
        if (classHandle == 0)
        {
            return 0;
        }

        lock (_commandSync)
        {
            MonoPipeClient pipe = RequirePipe();
            pipe.SendCommand(MonoDataCollectorCommand.GetStaticFieldAddressFromClass);
            pipe.WriteQWord(domainHandle);
            pipe.WriteQWord(classHandle);
            return pipe.ReadQWord();
        }
    }

    public ulong GetStaticFieldValue(ulong vtableHandle, ulong fieldHandle)
    {
        if (fieldHandle == 0)
        {
            return 0;
        }

        lock (_commandSync)
        {
            MonoPipeClient pipe = RequirePipe();
            pipe.SendCommand(MonoDataCollectorCommand.GetStaticFieldValue);
            pipe.WriteQWord(vtableHandle);
            pipe.WriteQWord(fieldHandle);
            return pipe.ReadQWord();
        }
    }

    public ulong GetObjectClass(ulong objectAddress)
    {
        if (objectAddress == 0)
        {
            return 0;
        }

        lock (_commandSync)
        {
            MonoPipeClient pipe = RequirePipe();
            pipe.SendCommand(MonoDataCollectorCommand.GetObjectClass);
            pipe.WriteQWord(objectAddress);
            return pipe.ReadQWord();
        }
    }

    public ulong GetFieldClass(ulong fieldHandle)
    {
        if (fieldHandle == 0)
        {
            return 0;
        }

        lock (_commandSync)
        {
            MonoPipeClient pipe = RequirePipe();
            pipe.SendCommand(MonoDataCollectorCommand.GetFieldClass);
            pipe.WriteQWord(fieldHandle);
            return pipe.ReadQWord();
        }
    }

    public ulong GetFieldType(ulong fieldHandle)
    {
        if (fieldHandle == 0)
        {
            return 0;
        }

        lock (_commandSync)
        {
            MonoPipeClient pipe = RequirePipe();
            pipe.SendCommand(MonoDataCollectorCommand.GetFieldType);
            pipe.WriteQWord(fieldHandle);
            return pipe.ReadQWord();
        }
    }

    public ulong GetClassType(ulong classHandle)
    {
        if (classHandle == 0)
        {
            return 0;
        }

        lock (_commandSync)
        {
            MonoPipeClient pipe = RequirePipe();
            pipe.SendCommand(MonoDataCollectorCommand.GetClassType);
            pipe.WriteQWord(classHandle);
            return pipe.ReadQWord();
        }
    }

    public ulong GetClassFromType(ulong typeHandle)
    {
        if (typeHandle == 0)
        {
            return 0;
        }

        lock (_commandSync)
        {
            MonoPipeClient pipe = RequirePipe();
            pipe.SendCommand(MonoDataCollectorCommand.GetClassOfType);
            pipe.WriteQWord(typeHandle);
            return pipe.ReadQWord();
        }
    }

    public ulong GetPointerClass(ulong typeHandle)
    {
        if (typeHandle == 0)
        {
            return 0;
        }

        lock (_commandSync)
        {
            MonoPipeClient pipe = RequirePipe();
            pipe.SendCommand(MonoDataCollectorCommand.GetPointerTypeClass);
            pipe.WriteQWord(typeHandle);
            return pipe.ReadQWord();
        }
    }

    public ulong GetPointerType(ulong typeHandle)
    {
        if (typeHandle == 0)
        {
            return 0;
        }

        lock (_commandSync)
        {
            MonoPipeClient pipe = RequirePipe();
            pipe.SendCommand(MonoDataCollectorCommand.GetPointerTypeOfType);
            pipe.WriteQWord(typeHandle);
            return pipe.ReadQWord();
        }
    }

    public MonoTypeCode GetTypeCode(ulong typeHandle)
    {
        if (typeHandle == 0)
        {
            return MonoTypeCode.End;
        }

        lock (_commandSync)
        {
            MonoPipeClient pipe = RequirePipe();
            pipe.SendCommand(MonoDataCollectorCommand.GetTypeOfMonoType);
            pipe.WriteQWord(typeHandle);
            return (MonoTypeCode)pipe.ReadDWord();
        }
    }

    public bool IsClassEnum(ulong classHandle)
    {
        if (classHandle == 0)
        {
            return false;
        }

        lock (_commandSync)
        {
            MonoPipeClient pipe = RequirePipe();
            pipe.SendCommand(MonoDataCollectorCommand.IsClassEnum);
            pipe.WriteQWord(classHandle);
            return pipe.ReadByte() != 0;
        }
    }

    public bool IsClassValueType(ulong classHandle)
    {
        if (classHandle == 0)
        {
            return false;
        }

        lock (_commandSync)
        {
            MonoPipeClient pipe = RequirePipe();
            pipe.SendCommand(MonoDataCollectorCommand.IsClassValueType);
            pipe.WriteQWord(classHandle);
            return pipe.ReadByte() != 0;
        }
    }

    public bool IsClassGeneric(ulong classHandle)
    {
        if (classHandle == 0)
        {
            return false;
        }

        lock (_commandSync)
        {
            MonoPipeClient pipe = RequirePipe();
            pipe.SendCommand(MonoDataCollectorCommand.IsClassGeneric);
            pipe.WriteQWord(classHandle);
            return pipe.ReadByte() != 0;
        }
    }

    public MonoImageDump? DumpImage(string imageName)
    {
        if (string.IsNullOrWhiteSpace(imageName))
        {
            return null;
        }

        lock (_commandSync)
        {
            MonoPipeClient pipe = RequirePipe();
            pipe.SendCommand(MonoDataCollectorCommand.DumpImage);
            pipe.WriteString(imageName);
            int length = CheckedLength(pipe.ReadDWord(), "image dump");
            return length == 0
                ? null
                : new MonoImageDump(imageName, pipe.ReadBytes(length));
        }
    }

    public void Dispose()
    {
        lock (_commandSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_pipe is not null)
            {
                try
                {
                    if (_pipe.IsConnected)
                    {
                        _pipe.SendCommand(MonoDataCollectorCommand.Terminate);
                    }
                }
                catch
                {
                }

                _pipe.Dispose();
                _pipe = null;
            }
        }
    }

    private void ConnectCore()
    {
        lock (_commandSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pipe?.Dispose();
            _pipe = new MonoPipeClient(_processId);

            _pipe.SendCommand(MonoDataCollectorCommand.InitializeRuntime);
            if (_pipe.ReadByte() == 0)
            {
                throw new InvalidOperationException(
                    "Mono DataCollector could not initialize the runtime.");
            }

            _pipe.SendCommand(MonoDataCollectorCommand.GetDataCollectorVersion);
            ProtocolVersion = _pipe.ReadDWord();
            if (ProtocolVersion != ExpectedProtocolVersion)
            {
                throw new InvalidOperationException(
                    $"Mono Data Collector protocol mismatch. " +
                    $"Expected {ExpectedProtocolVersion}, got {ProtocolVersion}.");
            }

            _pipe.SendCommand(MonoDataCollectorCommand.IsIl2Cpp);
            IsIl2Cpp = _pipe.ReadByte() == 1;
        }
    }

    private static List<ulong> EnumerateAssemblyHandles(MonoPipeClient pipe)
    {
        pipe.SendCommand(MonoDataCollectorCommand.EnumerateAssemblies);
        uint count = pipe.ReadDWord();
        List<ulong> assemblies = new(CheckedCapacity(count, "assembly count"));
        for (uint index = 0; index < count; index++)
        {
            ulong handle = pipe.ReadQWord();
            if (handle != 0)
            {
                assemblies.Add(handle);
            }
        }

        return assemblies;
    }

    private static void CollectFields(
        MonoPipeClient pipe,
        ulong classHandle,
        List<MonoFieldInfo> fields)
    {
        pipe.SendCommand(MonoDataCollectorCommand.EnumerateFieldsInClass);
        pipe.WriteQWord(classHandle);
        while (true)
        {
            ulong fieldHandle = pipe.ReadQWord();
            if (fieldHandle == 0)
            {
                return;
            }

            ulong typeHandle = pipe.ReadQWord();
            MonoTypeCode typeCode = (MonoTypeCode)pipe.ReadDWord();
            ulong declaringClass = pipe.ReadQWord();
            int offset = unchecked((int)pipe.ReadDWord());
            MonoFieldAttributes attributes =
                (MonoFieldAttributes)pipe.ReadDWord();
            string name = pipe.ReadString();
            string typeName = pipe.ReadString();
            fields.Add(new MonoFieldInfo(
                fieldHandle,
                typeHandle,
                typeCode,
                declaringClass,
                offset,
                attributes,
                name,
                typeName));
        }
    }

    private static void CollectMethods(
        MonoPipeClient pipe,
        ulong classHandle,
        List<MonoMethodInfo> methods)
    {
        pipe.SendCommand(MonoDataCollectorCommand.EnumerateMethodsInClass);
        pipe.WriteQWord(classHandle);
        while (true)
        {
            ulong methodHandle = pipe.ReadQWord();
            if (methodHandle == 0)
            {
                return;
            }

            string name = pipe.ReadString();
            MonoMethodAttributes attributes =
                (MonoMethodAttributes)pipe.ReadDWord();
            methods.Add(new MonoMethodInfo(methodHandle, name, attributes));
        }
    }

    private static ulong GetParentClassCore(
        MonoPipeClient pipe,
        ulong classHandle)
    {
        pipe.SendCommand(MonoDataCollectorCommand.GetParentClass);
        pipe.WriteQWord(classHandle);
        return pipe.ReadQWord();
    }

    private static IReadOnlyList<MonoClassInfo> GetClassesLegacy(
        MonoPipeClient pipe,
        ulong imageHandle)
    {
        pipe.SendCommand(MonoDataCollectorCommand.EnumerateClassesInImage);
        pipe.WriteQWord(imageHandle);
        uint count = pipe.ReadDWord();
        List<MonoClassInfo> classes = new(CheckedCapacity(count, "class count"));
        for (uint index = 0; index < count; index++)
        {
            ulong handle = pipe.ReadQWord();
            if (handle == 0)
            {
                continue;
            }

            string name = pipe.ReadString();
            string nameSpace = pipe.ReadString();
            string fullName = string.IsNullOrWhiteSpace(nameSpace)
                ? name
                : $"{nameSpace}.{name}";
            classes.Add(new MonoClassInfo(
                handle,
                0,
                0,
                name,
                nameSpace,
                fullName));
        }

        return classes;
    }

    private MonoPipeClient RequirePipe()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _pipe is { IsConnected: true } pipe
            ? pipe
            : throw new InvalidOperationException(
                "Mono Data Collector is not connected.");
    }

    private static int CheckedLength(uint value, string description)
    {
        if (value > int.MaxValue)
        {
            throw new InvalidDataException(
                $"Mono Data Collector returned an invalid {description} length.");
        }

        return (int)value;
    }

    private static int CheckedCapacity(uint value, string description)
    {
        if (value > 1_000_000)
        {
            throw new InvalidDataException(
                $"Mono Data Collector returned an unreasonable {description}: {value}.");
        }

        return (int)value;
    }

    private sealed class ProtocolReader
    {
        private readonly byte[] _data;
        private int _position;

        public ProtocolReader(byte[] data)
        {
            _data = data;
        }

        public uint ReadUInt32(string fieldName)
        {
            ReadOnlySpan<byte> value = Read(sizeof(uint), fieldName);
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(value);
        }

        public ulong ReadUInt64(string fieldName)
        {
            ReadOnlySpan<byte> value = Read(sizeof(ulong), fieldName);
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(value);
        }

        public string ReadWordString(string fieldName)
        {
            ReadOnlySpan<byte> lengthBytes = Read(sizeof(ushort), fieldName);
            ushort length = System.Buffers.Binary.BinaryPrimitives
                .ReadUInt16LittleEndian(lengthBytes);
            return length == 0
                ? string.Empty
                : Encoding.UTF8.GetString(Read(length, fieldName));
        }

        private ReadOnlySpan<byte> Read(int length, string fieldName)
        {
            if (length < 0 || _position > _data.Length - length)
            {
                throw new InvalidDataException(
                    $"Mono Data Collector returned a truncated {fieldName}.");
            }

            ReadOnlySpan<byte> value = _data.AsSpan(_position, length);
            _position += length;
            return value;
        }
    }
}
