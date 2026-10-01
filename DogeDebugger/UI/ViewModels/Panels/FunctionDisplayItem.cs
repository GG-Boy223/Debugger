using DogeDebugger.Core.CrossReference;

namespace DogeDebugger.UI.ViewModels.Panels;

public sealed record FunctionDisplayItem(
    uint StartRva,
    uint EndRva,
    string DisplayName,
    FunctionSource Source,
    int XrefCount,
    ulong BaseAddress)
{
    public ulong StartVa => BaseAddress + StartRva;

    public uint Size => EndRva > StartRva ? EndRva - StartRva : 0;

    public string StartRvaHex => $"+{StartRva:X}";

    public string StartVaHex => $"{StartVa:X}";

    public string SourceTag => Source switch
    {
        FunctionSource.Pdata => "pdata",
        FunctionSource.Export => "export",
        FunctionSource.Pdb => "pdb",
        _ => "?"
    };
}
