using CommunityToolkit.Mvvm.ComponentModel;
using DogeDebugger.Core.Search;

namespace DogeDebugger.UI.ViewModels.Panels;

public sealed partial class SavedAddressRow : ObservableObject
{
    [ObservableProperty]
    private bool _isFrozen;

    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private bool _isHexadecimal;

    [ObservableProperty]
    private bool _isSigned;

    public required ulong Address { get; init; }

    public string Description { get; set; } = string.Empty;

    public MemoryValueKind ValueKind { get; init; } = MemoryValueKind.Int32;

    public string AddressText => $"0x{Address:X}";

    public string TypeText => ValueKind switch
    {
        MemoryValueKind.Byte => "Byte",
        MemoryValueKind.SByte => "SByte",
        MemoryValueKind.Int16 => "2 Bytes",
        MemoryValueKind.UInt16 => "2 Bytes unsigned",
        MemoryValueKind.Int32 => "4 Bytes",
        MemoryValueKind.UInt32 => "4 Bytes unsigned",
        MemoryValueKind.Int64 => "8 Bytes",
        MemoryValueKind.UInt64 => "8 Bytes unsigned",
        MemoryValueKind.Single => "Float",
        MemoryValueKind.Double => "Double",
        MemoryValueKind.Utf8String => "UTF-8",
        MemoryValueKind.Utf16String => "UTF-16",
        MemoryValueKind.ByteArray => "Array of Bytes",
        _ => ValueKind.ToString()
    };
}
