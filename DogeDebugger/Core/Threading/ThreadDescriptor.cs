namespace DogeDebugger.Core.Threading;

public sealed class ThreadDescriptor
{
    public uint ThreadId { get; set; }

    public uint ProcessId { get; set; }

    public int BasePriority { get; set; }

    public uint SuspendCount { get; set; }

    public ulong StartAddress { get; set; }

    public ulong InstructionPointer { get; set; }

    public ulong TebBaseAddress { get; set; }

    public string Name { get; set; } = string.Empty;

    public string StartAddressText => $"0x{StartAddress:X}";

    public string InstructionPointerText => $"0x{InstructionPointer:X}";

    public string TebBaseAddressText => $"0x{TebBaseAddress:X}";

    public string BasePriorityText => BasePriority.ToString();

    public string SuspendCountText => SuspendCount.ToString();
}
