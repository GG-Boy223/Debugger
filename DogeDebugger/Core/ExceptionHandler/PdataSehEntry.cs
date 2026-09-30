namespace DogeDebugger.Core.ExceptionHandler;

public sealed class PdataSehEntry
{
    public int Index { get; set; }

    public ulong FunctionStart { get; set; }

    public ulong FunctionEnd { get; set; }

    public ulong HandlerAddress { get; set; }

    public string ModuleName { get; set; } = string.Empty;

    public string HandlerSymbol { get; set; } = string.Empty;

    public byte Flags { get; set; }

    public string DisplayFunctionRange => $"0x{FunctionStart:X} - 0x{FunctionEnd:X}";

    public string DisplayHandler => $"0x{HandlerAddress:X}";

    public string FlagNames
    {
        get
        {
            List<string> names = [];
            if ((Flags & 1) != 0)
            {
                names.Add("EHANDLER");
            }

            if ((Flags & 2) != 0)
            {
                names.Add("UHANDLER");
            }

            if ((Flags & 4) != 0)
            {
                names.Add("CHAININFO");
            }

            return names.Count == 0 ? "-" : string.Join("|", names);
        }
    }
}
