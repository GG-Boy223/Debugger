using DogeDebugger.Core.Native;

namespace DogeDebugger.Core.Memory;

public sealed class MemoryRegionInfo
{
    public ulong BaseAddress { get; set; }

    public ulong AllocationBase { get; set; }

    public ulong Size { get; set; }

    public uint State { get; set; }

    public uint Type { get; set; }

    public uint AllocationProtect { get; set; }

    public uint Protect { get; set; }

    public string AllocationProtectText { get; set; } = string.Empty;

    public string ProtectText { get; set; } = string.Empty;

    public string AllocationProtectDisplayText =>
        FormatProtectionDisplay(AllocationProtect);

    public string ProtectDisplayText =>
        FormatProtectionDisplay(Protect);

    public string ModuleName { get; set; } = string.Empty;

    public string SectionName { get; set; } = string.Empty;

    public bool IsModuleHeader { get; set; }

    public bool IsReadable =>
        (Protect & 0xEE) != 0 &&
        (Protect & 0x101) == 0;

    public bool IsWritable =>
        (Protect & 0xCC) != 0 &&
        (Protect & 0x101) == 0;

    public bool IsExecutable =>
        (Protect & 0xF0) != 0 &&
        (Protect & 0x101) == 0;

    public bool IsCopyOnWrite =>
        (Protect & (NativeMethods.PageWriteCopy | NativeMethods.PageExecuteWriteCopy)) != 0;

    public string BaseAddressText => $"{BaseAddress:X16}";

    public string AllocationBaseText => $"{AllocationBase:X16}";

    public string SizeText => $"{Size:X}";

    public string StateText => State switch
    {
        0x1000 => "Commit",
        0x2000 => "Reserve",
        0x10000 => "Free",
        _ => $"0x{State:X}"
    };

    public string TypeText => Type switch
    {
        0x1000000 => "Image",
        0x40000 => "Mapped",
        0x20000 => "Private",
        _ => $"0x{Type:X}"
    };

    private static string FormatProtectionDisplay(uint protection)
    {
        if (protection == 0)
        {
            return string.Empty;
        }

        string access = (protection & 0xFF) switch
        {
            NativeMethods.PageNoAccess => "No Access",
            NativeMethods.PageReadOnly => "Read",
            NativeMethods.PageReadWrite => "Read/Write",
            NativeMethods.PageWriteCopy => "Write Copy",
            NativeMethods.PageExecute => "Execute",
            NativeMethods.PageExecuteRead => "Execute Read",
            NativeMethods.PageExecuteReadWrite => "Execute Read/Write",
            NativeMethods.PageExecuteWriteCopy => "Execute Write Copy",
            _ => "Unknown"
        };

        if ((protection & NativeMethods.PageGuard) != 0)
        {
            access += " +Guard";
        }

        if ((protection & NativeMethods.PageNoCache) != 0)
        {
            access += " +NoCache";
        }

        if ((protection & NativeMethods.PageWriteCombine) != 0)
        {
            access += " +WriteCombine";
        }

        return access;
    }
}
