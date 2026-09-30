namespace DogeDebugger.UI.ViewModels.Panels;

public sealed record XrefDisplayItem(
    ulong SourceVa,
    string TypeText,
    string CallerName,
    uint CallerOffset,
    ulong CallerStartVa)
{
    public string SourceVaHex => $"0x{SourceVa:X}";

    public string CallerDisplay => CallerOffset == 0
        ? CallerName
        : $"{CallerName}+0x{CallerOffset:X}";

    public string CallerAddressHex => $"0x{CallerStartVa:X}";
}
