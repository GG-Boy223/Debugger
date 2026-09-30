using CommunityToolkit.Mvvm.ComponentModel;

namespace DogeDebugger.Core.ExceptionHandler;

public enum VehEntryType
{
    VectoredExceptionHandler,
    VectoredContinueHandler
}

public sealed class VehEntry : ObservableObject
{
    public int Index { get; set; }

    public ulong NodeAddress { get; set; }

    public ulong EncodedPointer { get; set; }

    public ulong DecodedAddress { get; set; }

    public string ModuleName { get; set; } = string.Empty;

    public string SymbolName { get; set; } = string.Empty;

    public VehEntryType Type { get; set; }

    public string DisplayNodeAddress => $"0x{NodeAddress:X}";

    public string DisplayEncodedPointer => $"0x{EncodedPointer:X}";

    public string DisplayDecodedAddress => $"0x{DecodedAddress:X}";

    public string TypeName => Type switch
    {
        VehEntryType.VectoredExceptionHandler => "VEH",
        VehEntryType.VectoredContinueHandler => "VCH",
        _ => "Unknown"
    };
}
