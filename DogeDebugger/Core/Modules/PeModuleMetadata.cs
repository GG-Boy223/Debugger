namespace DogeDebugger.Core.Modules;

public sealed class PeModuleMetadata
{
    public string FilePath { get; init; } = string.Empty;

    public ushort Machine { get; init; }

    public ushort Characteristics { get; init; }

    public bool Is64Bit { get; init; }

    public bool IsDll => (Characteristics & 0x2000) != 0;

    public ulong ImageBase { get; init; }

    public uint SizeOfImage { get; init; }

    public uint SizeOfHeaders { get; init; }

    public uint EntryPointRva { get; init; }

    public uint Timestamp { get; init; }

    public ushort Subsystem { get; init; }

    public IReadOnlyList<PeSection> Sections { get; init; } = [];

    public IReadOnlyList<PeImport> Imports { get; init; } = [];

    public IReadOnlyList<PeExport> Exports { get; init; } = [];

    public IReadOnlyList<PeRuntimeFunction> RuntimeFunctions { get; init; } = [];
}

public sealed class PeSection
{
    public string Name { get; init; } = string.Empty;

    public uint VirtualAddress { get; init; }

    public uint VirtualSize { get; init; }

    public uint RawOffset { get; init; }

    public uint RawSize { get; init; }

    public uint Characteristics { get; init; }

    public bool IsExecutable => (Characteristics & 0x20000000) != 0;

    public bool IsReadable => (Characteristics & 0x40000000) != 0;

    public bool IsWritable => (Characteristics & 0x80000000) != 0;
}

public sealed class PeImport
{
    public string ModuleName { get; init; } = string.Empty;

    public string? FunctionName { get; init; }

    public ushort? Ordinal { get; init; }

    public uint IatRva { get; init; }

    public string DisplayAddress(ulong moduleBase) =>
        $"0x{moduleBase + IatRva:X}";

    public string FunctionDisplayName => FunctionName ??
        (Ordinal is { } ordinal ? $"#{ordinal}" : string.Empty);
}

public sealed class PeExport
{
    public string Name { get; init; } = string.Empty;

    public uint Ordinal { get; init; }

    public uint FunctionRva { get; init; }

    public string? ForwarderName { get; init; }

    public string DisplayAddress(ulong moduleBase) =>
        $"0x{moduleBase + FunctionRva:X}";

    public string OrdinalText => $"#{Ordinal}";
}

public sealed class PeRuntimeFunction
{
    public uint BeginRva { get; init; }

    public uint EndRva { get; init; }

    public uint UnwindInfoRva { get; init; }
}
