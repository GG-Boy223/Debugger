using DogeDebugger.Core.Modules;

namespace DogeDebugger.Plugins.UnrealEngine.Models;

public sealed class UnrealSession
{
    public required ModuleDescriptor Module { get; init; }

    public required UnrealOffsets Offsets { get; init; }

    public string VersionText { get; set; } = "Unknown";

    public int NameCount { get; set; }

    public int ObjectCount { get; set; }

    public bool Truncated { get; set; }

    public IReadOnlyList<UnrealObjectInfo> Objects { get; set; } = [];

    public IReadOnlyList<UnrealObjectInfo> Packages { get; set; } = [];

    public IReadOnlyList<UnrealTypeInfo> Types { get; set; } = [];

    public UnrealDiagnosticLog Diagnostics { get; set; } = new();

    public int ClassCount => Types.Count(type => type.Kind == UnrealObjectKind.Class);

    public int StructCount => Types.Count(type => type.Kind == UnrealObjectKind.Struct);

    public int FunctionCount => Types.Count(type => type.Kind == UnrealObjectKind.Function);

    public int EnumCount => Types.Count(type => type.Kind == UnrealObjectKind.Enum);
}
