namespace DogeDebugger.Plugins.UnrealEngine.Models;

public sealed class UnrealTypeInfo
{
    public required UnrealObjectInfo Object { get; init; }

    public UnrealObjectKind Kind => Object.Kind;

    public string Name => Object.Name;

    public string FullName => Object.FullName;

    public string ClassName => Object.ClassName;

    public string AddressHex => Object.AddressHex;

    public string KindText => Object.KindText;
}
