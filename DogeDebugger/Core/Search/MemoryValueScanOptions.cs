using System.Text;

namespace DogeDebugger.Core.Search;

public sealed class MemoryValueScanOptions
{
    public MemoryValueKind Kind { get; set; } = MemoryValueKind.Int32;

    public MemoryValueComparison Comparison { get; set; } = MemoryValueComparison.Exact;

    public string Value { get; set; } = string.Empty;

    public string? SecondValue { get; set; }

    public string? ModuleName { get; set; }

    public ulong StartAddress { get; set; }

    public ulong EndAddress { get; set; } = ulong.MaxValue;

    public int Alignment { get; set; } = 1;

    public int MaximumResults { get; set; } = 100_000;

    public bool SearchPrivateMemory { get; set; } = true;

    public bool SearchImageMemory { get; set; } = true;

    public bool SearchMappedMemory { get; set; }

    public bool WritableOnly { get; set; }

    public bool ExecutableOnly { get; set; }

    public bool? RequireWritable { get; set; }

    public bool? RequireExecutable { get; set; }

    public bool? RequireCopyOnWrite { get; set; }

    public bool PauseWhileScanning { get; set; }

    public bool IgnoreCase { get; set; }

    public Encoding TextEncoding { get; set; } = Encoding.UTF8;

    public bool IncludeAddressListStrings { get; set; }

    public bool ZeroTerminate { get; set; }

    public int BitStart { get; set; }

    public int BitLength { get; set; }
}
