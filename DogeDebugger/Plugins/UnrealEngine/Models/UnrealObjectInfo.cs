namespace DogeDebugger.Plugins.UnrealEngine.Models;

public sealed class UnrealObjectInfo
{
    public int Index { get; init; }

    public ulong Address { get; init; }

    public ulong ClassAddress { get; init; }

    public ulong OuterAddress { get; init; }

    public string Name { get; init; } = string.Empty;

    public string ClassName { get; init; } = string.Empty;

    public string OuterName { get; init; } = string.Empty;

    public string PackageName { get; init; } = string.Empty;

    public string PathName { get; init; } = string.Empty;

    public string FullName { get; init; } = string.Empty;

    public UnrealObjectKind Kind { get; init; }

    public string IndexHex => Index < 0 ? string.Empty : $"{Index:X8}";

    public string AddressHex => Address == 0 ? string.Empty : $"0x{Address:X}";

    public string ClassAddressHex => ClassAddress == 0
        ? string.Empty
        : $"0x{ClassAddress:X}";

    public string OuterAddressHex => OuterAddress == 0
        ? string.Empty
        : $"0x{OuterAddress:X}";

    public string KindText => Kind switch
    {
        UnrealObjectKind.Package => "Package",
        UnrealObjectKind.Class => "Class",
        UnrealObjectKind.Struct => "Struct",
        UnrealObjectKind.Function => "Function",
        UnrealObjectKind.Enum => "Enum",
        _ => "Object"
    };

    public bool Matches(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return true;
        }

        return Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               ClassName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               FullName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               PathName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               AddressHex.Contains(term, StringComparison.OrdinalIgnoreCase);
    }
}
