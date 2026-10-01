using DogeDebugger.Core.Modules;

namespace DogeDebugger.Core.Disassembly;

public enum CommandSearchMatchMode
{
    ExactBytes,
    Semantic
}

public enum InstructionReferenceKind
{
    Address,
    Constant
}

public sealed class InstructionReferenceSearchRequest
{
    public required ulong Value { get; init; }

    public InstructionReferenceKind Kind { get; init; }

    public int Bitness { get; init; } = 64;

    public AssemblySyntax SyntaxFormat { get; init; } = AssemblySyntax.Intel;

    public IReadOnlyList<ModuleDescriptor> ScanModules { get; init; } = [];

    public int MaxResults { get; init; } = 200_000;
}

public sealed class CommandSearchRequest
{
    public required string AssemblyText { get; init; }

    public ulong AssemblyAddress { get; init; }

    public int Bitness { get; init; } = 64;

    public AssemblySyntax SyntaxFormat { get; init; } = AssemblySyntax.Intel;

    public IReadOnlyList<ModuleDescriptor> ScanModules { get; init; } = [];

    public int MaxResults { get; init; } = 200_000;
}

public sealed class CommandSearchResultItem
{
    public ulong Address { get; init; }

    public string ModuleName { get; init; } = string.Empty;

    public ulong ModuleBase { get; init; }

    public ulong ModuleOffset { get; init; }

    public byte[] Bytes { get; init; } = [];

    public string Disassembly { get; init; } = string.Empty;

    public string AddressText => $"{Address:X16}";

    public string ModuleOffsetText =>
        string.IsNullOrWhiteSpace(ModuleName)
            ? $"0x{Address:X}"
            : $"{ModuleName}+{ModuleOffset:X}";

    public string BytesText =>
        string.Join(' ', Bytes.Select(static value => value.ToString("X2")));
}

public sealed class CommandSearchScanResult
{
    public IReadOnlyList<CommandSearchResultItem> Results { get; init; } = [];

    public byte[] PatternBytes { get; init; } = [];

    public CommandSearchMatchMode MatchMode { get; init; }

    public ulong ScannedBytes { get; init; }

    public int ScannedRegionCount { get; init; }

    public bool Truncated { get; init; }

    public TimeSpan Elapsed { get; init; }
}
