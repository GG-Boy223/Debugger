using System.IO;
using System.Text.Json;
using DogeDebugger.Core.Process;

namespace DogeDebugger.Core.Rtti;

public sealed class RttiWorkspaceService
{
    private readonly object _gate = new();
    private readonly List<RttiClassDefinition> _classes = [];
    private readonly RttiAutoGuesser _autoGuesser = new();

    public event Action? Changed;

    public IReadOnlyList<RttiClassDefinition> GetClasses()
    {
        lock (_gate)
        {
            return _classes.ToArray();
        }
    }

    public RttiClassDefinition? FindClass(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            return _classes.FirstOrDefault(definition =>
                string.Equals(
                    definition.Name,
                    name,
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    public RttiClassDefinition CreateStruct(
        string name,
        ulong baseAddress,
        string comment,
        IEnumerable<RttiFieldDefinition> fields,
        bool is64Bit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(fields);

        RttiClassDefinition definition = new()
        {
            Name = name.Trim(),
            BaseAddress = baseAddress,
            Comment = comment ?? string.Empty
        };
        foreach (RttiFieldDefinition field in fields)
        {
            ArgumentNullException.ThrowIfNull(field);
            definition.Fields.Add(field);
        }

        definition.RecalculateOffsets(is64Bit);
        lock (_gate)
        {
            RttiClassDefinition? existing = _classes.FirstOrDefault(item =>
                string.Equals(item.Name, definition.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                _classes.Remove(existing);
            }

            _classes.Add(definition);
        }

        Changed?.Invoke();
        return definition;
    }

    public RttiClassDefinition AutoGuess(
        ITargetProcess process,
        string name,
        ulong baseAddress,
        int length,
        string comment = "")
    {
        RttiClassDefinition definition = _autoGuesser.Guess(
            process,
            name,
            baseAddress,
            length,
            comment);
        lock (_gate)
        {
            _classes.Add(definition);
        }

        Changed?.Invoke();
        return definition;
    }

    public bool RemoveStruct(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        bool removed;
        lock (_gate)
        {
            RttiClassDefinition? definition = _classes.FirstOrDefault(item =>
                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            removed = definition is not null && _classes.Remove(definition);
        }

        if (removed)
        {
            Changed?.Invoke();
        }

        return removed;
    }

    public bool AddField(
        string structName,
        RttiFieldDefinition field,
        bool is64Bit,
        int? insertIndex = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(structName);
        ArgumentNullException.ThrowIfNull(field);

        RttiClassDefinition? definition;
        lock (_gate)
        {
            definition = _classes.FirstOrDefault(item =>
                string.Equals(item.Name, structName, StringComparison.OrdinalIgnoreCase));
            if (definition is null)
            {
                return false;
            }

            if (field.Type == RttiNodeType.Pointer && field.PointerSize is not (4 or 8))
            {
                field.PointerSize = is64Bit ? 8 : 4;
            }

            int index = insertIndex.HasValue
                ? Math.Clamp(insertIndex.Value, 0, definition.Fields.Count)
                : definition.Fields.Count;
            definition.Fields.Insert(index, field);
            definition.RecalculateOffsets(is64Bit);
        }

        Changed?.Invoke();
        return true;
    }

    public RttiClassDefinition GetRequiredClass(string name)
    {
        return FindClass(name) ??
               throw new KeyNotFoundException($"Struct not found: {name}");
    }

    public RttiInstance ReadInstance(
        ITargetProcess process,
        RttiClassDefinition definition,
        ulong? address = null)
    {
        ulong effectiveAddress = address ?? definition.BaseAddress;
        definition.BaseAddress = effectiveAddress;
        return RttiMemoryReader.ReadInstance(process, definition, effectiveAddress);
    }

    public IReadOnlyList<RttiFieldValue> ReadValues(
        ITargetProcess process,
        RttiClassDefinition definition,
        ulong? address = null)
    {
        RttiInstance instance = ReadInstance(process, definition, address);
        return RttiMemoryReader.ReadValues(process, instance);
    }

    public void SaveProject(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        RttiProjectDocument document = new()
        {
            Classes = GetClasses().Select(definition => new RttiProjectClass
            {
                Name = definition.Name,
                Comment = definition.Comment,
                BaseAddress = definition.BaseAddress,
                IsExpanded = definition.IsExpanded,
                Fields = definition.Fields.Select(field => new RttiProjectField
                {
                    Name = field.Name,
                    Comment = field.Comment,
                    Offset = field.Offset,
                    Type = field.Type.ToString(),
                    Length = field.Length,
                    PointerSize = field.PointerSize,
                    PointedType = field.PointedType?.ToString(),
                    PointedClass = field.PointedClass,
                    ArrayIndex = field.ArrayIndex
                }).ToList()
            }).ToList()
        };

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                document,
                new JsonSerializerOptions { WriteIndented = true }),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public void LoadProject(string path, bool is64Bit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        RttiProjectDocument document = JsonSerializer.Deserialize<RttiProjectDocument>(
                File.ReadAllText(path, System.Text.Encoding.UTF8))
            ?? throw new InvalidDataException("RTTI project file is empty or invalid.");

        List<RttiClassDefinition> definitions = [];
        foreach (RttiProjectClass source in document.Classes)
        {
            RttiClassDefinition definition = new()
            {
                Name = source.Name,
                Comment = source.Comment,
                BaseAddress = source.BaseAddress,
                IsExpanded = source.IsExpanded
            };
            foreach (RttiProjectField field in source.Fields)
            {
                if (!Enum.TryParse(field.Type, ignoreCase: true, out RttiNodeType type))
                {
                    throw new InvalidDataException($"Unknown field type: {field.Type}");
                }

                RttiNodeType? pointedType = null;
                if (!string.IsNullOrWhiteSpace(field.PointedType))
                {
                    if (!Enum.TryParse(field.PointedType, ignoreCase: true, out RttiNodeType parsed))
                    {
                        throw new InvalidDataException(
                            $"Unknown pointed field type: {field.PointedType}");
                    }

                    pointedType = parsed;
                }

                definition.Fields.Add(new RttiFieldDefinition
                {
                    Name = field.Name,
                    Comment = field.Comment,
                    Offset = field.Offset,
                    Type = type,
                    Length = field.Length,
                    PointerSize = field.PointerSize,
                    PointedType = pointedType,
                    PointedClass = field.PointedClass,
                    ArrayIndex = field.ArrayIndex
                });
            }

            definition.RecalculateOffsets(is64Bit);
            definitions.Add(definition);
        }

        lock (_gate)
        {
            _classes.Clear();
            _classes.AddRange(definitions);
        }

        Changed?.Invoke();
    }

    private sealed class RttiProjectDocument
    {
        public List<RttiProjectClass> Classes { get; set; } = [];
    }

    private sealed class RttiProjectClass
    {
        public string Name { get; set; } = string.Empty;

        public string Comment { get; set; } = string.Empty;

        public ulong BaseAddress { get; set; }

        public bool IsExpanded { get; set; } = true;

        public List<RttiProjectField> Fields { get; set; } = [];
    }

    private sealed class RttiProjectField
    {
        public string Name { get; set; } = string.Empty;

        public string Comment { get; set; } = string.Empty;

        public int Offset { get; set; }

        public string Type { get; set; } = string.Empty;

        public int Length { get; set; }

        public int PointerSize { get; set; }

        public string? PointedType { get; set; }

        public string? PointedClass { get; set; }

        public int? ArrayIndex { get; set; }
    }
}
