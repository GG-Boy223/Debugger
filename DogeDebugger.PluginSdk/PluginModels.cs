using System.Text.Json;

namespace DogeDebugger.PluginSdk;

public enum PluginContextMenuTarget
{
    Disassembly = 0,
    Hex = 1,
    AddressList = 2
}

public enum PluginBreakpointDecision
{
    Continue = 0,
    Break = 1,
    Suppress = 2
}

public enum PluginKeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8
}

public sealed class PluginKeyGesture
{
    public string Key { get; set; } = string.Empty;

    public PluginKeyModifiers Modifiers { get; set; }
}

public sealed class PluginModuleInfo
{
    public string Name { get; set; } = string.Empty;

    public string? Path { get; set; }

    public ulong BaseAddress { get; set; }

    public ulong Size { get; set; }

    public ulong EntryPoint { get; set; }
}

public sealed class PluginMemoryRegionInfo
{
    public ulong BaseAddress { get; set; }

    public ulong Size { get; set; }

    public string State { get; set; } = string.Empty;

    public string Protect { get; set; } = string.Empty;

    public string? ModuleName { get; set; }
}

public sealed class PluginDisassemblyLine
{
    public ulong Address { get; set; }

    public byte[] Bytes { get; set; } = [];

    public string Text { get; set; } = string.Empty;

    public string? Comment { get; set; }
}

public sealed class PluginDisassemblySelection
{
    public ulong? Address { get; set; }

    public IReadOnlyList<PluginDisassemblyLine> Lines { get; set; } = [];
}

public sealed class PluginSignatureOptions
{
    public ulong Address { get; set; }

    public int BeforeBytes { get; set; } = 16;

    public int AfterBytes { get; set; } = 32;

    public int MaxLength { get; set; } = 96;
}

public sealed class PluginSearchRequest
{
    public string Pattern { get; set; } = string.Empty;

    public ulong StartAddress { get; set; }

    public ulong EndAddress { get; set; }

    public int MaxResults { get; set; } = 1000;
}

public sealed class PluginSearchMatch
{
    public ulong Address { get; set; }

    public byte[] Bytes { get; set; } = [];
}

public sealed class PluginSearchResult
{
    public bool Truncated { get; set; }

    public IReadOnlyList<PluginSearchMatch> Matches { get; set; } = [];
}

public sealed class PluginCommandDescriptor
{
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public JsonElement InputSchema { get; set; }
}

public sealed class PluginEvent
{
    public string Name { get; set; } = string.Empty;

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    public JsonElement Data { get; set; }
}

public sealed class PluginBreakpointHitEvent
{
    public ulong Address { get; set; }

    public uint ProcessId { get; set; }

    public uint ThreadId { get; set; }

    public ulong InstructionPointer { get; set; }
}

public sealed class PluginMcpToolRequest
{
    public string Name { get; set; } = string.Empty;

    public JsonElement Arguments { get; set; }
}

public sealed class PluginMcpToolResult
{
    public bool IsError { get; set; }

    public string Text { get; set; } = string.Empty;

    public JsonElement? StructuredContent { get; set; }
}

public sealed class PluginMcpToolDescriptor
{
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public JsonElement InputSchema { get; set; }

    public Func<PluginMcpToolRequest, CancellationToken, ValueTask<PluginMcpToolResult>> Handler
        { get; set; } = static (_, _) =>
            ValueTask.FromResult(new PluginMcpToolResult { IsError = true, Text = "Tool handler is not configured." });
}

public sealed class PluginMenuItem
{
    public bool IsSeparator { get; set; }

    public string Header { get; set; } = string.Empty;

    public string? HotkeyId { get; set; }

    public Func<CancellationToken, ValueTask>? Handler { get; set; }

    public IReadOnlyList<PluginMenuItem> Items { get; set; } = [];
}
