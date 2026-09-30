using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using DogeDebugger.Plugins.CEMono.Protocol;

namespace DogeDebugger.Plugins.CEMono.UI;

public sealed record MonoImageItem(ulong Handle, string Name);

public sealed class MonoClassItem
{
    public MonoClassItem(
        ulong handle,
        string name,
        string nameSpace,
        string fullName)
    {
        Handle = handle;
        Name = name;
        Namespace = nameSpace;
        FullName = fullName;
    }

    public ulong Handle { get; }

    public string Name { get; }

    public string Namespace { get; }

    public string FullName { get; }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(FullName)
            ? string.IsNullOrWhiteSpace(Namespace)
                ? Name
                : $"{Namespace}.{Name}"
            : FullName;
}

public sealed class MonoStaticFieldRow
{
    public required string Name { get; init; }

    public required string TypeName { get; init; }

    public required string Address { get; init; }

    public required string Value { get; init; }
}

public sealed class MonoMethodRow
{
    public required string Name { get; init; }

    public required string Parameters { get; init; }

    public ulong Handle { get; init; }

    public string ClassName { get; init; } = string.Empty;

    public string NativeAddress { get; set; } = string.Empty;
}

public sealed partial class MonoFieldRow : ObservableObject
{
    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private string _value = string.Empty;

    public int Offset { get; init; }

    public int ValueOffset { get; init; }

    public required string Name { get; init; }

    public required string TypeName { get; init; }

    public ulong FieldHandle { get; init; }

    public MonoTypeCode MonoType { get; init; }

    public MonoFieldRow? Parent { get; init; }

    public bool IsInlineValueType { get; init; }

    public ulong TypeClassHandle { get; init; }

    public int Level { get; init; }

    public bool IsExpandable { get; init; }

    public int LoadedChildCount { get; set; }

    public string OffsetText =>
        Offset >= 0
            ? $"0x{Offset:X}"
            : $"-0x{-Offset:X}";

    public Thickness NameMargin =>
        new(Level * 16, 0, 0, 0);
}

public sealed record MonoInheritanceItem(ulong Handle, string Name)
{
    public string AddressText => $"0x{Handle:X}";
}
