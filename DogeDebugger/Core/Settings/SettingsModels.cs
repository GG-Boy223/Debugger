using System.Text.Json;
using System.Text.Json.Serialization;

namespace DogeDebugger.Core.Settings;

public sealed class DebugEventSettings
{
    public bool SystemBreakpoint { get; set; }

    public bool EntryBreakpoint { get; set; }

    public bool NtTerminateProcess { get; set; }

    public bool TlsCallbacks { get; set; }

    public bool TlsCallbacksSystem { get; set; }

    public bool ThreadEntry { get; set; }

    public bool ThreadStart { get; set; }

    public bool ThreadEnd { get; set; }

    public bool ThreadNameSet { get; set; }

    public bool DllEntry { get; set; }

    public bool DllEntrySystem { get; set; }

    public bool DllLoad { get; set; }

    public bool DllLoadSystem { get; set; }

    public bool DllUnload { get; set; }

    public bool DllUnloadSystem { get; set; }

    public bool DebugStrings { get; set; }
}

public enum ExceptionAction
{
    Break = 0,
    Continue = 1,
    Ignore = 2
}

public sealed class ExceptionRule
{
    public uint ExceptionCode { get; set; }

    public string Name { get; set; } = string.Empty;

    public ExceptionAction Action { get; set; }
}

public sealed class ExceptionHandlingSettings
{
    public int Mode { get; set; }

    public bool LogNonDebuggerExceptions { get; set; }

    public bool SuppressStaleBreakpointEvents { get; set; } = true;

    public List<ExceptionRule> CustomRules { get; set; } =
    [
        new() { ExceptionCode = 0x80000003, Name = "BREAKPOINT (INT3)", Action = ExceptionAction.Break },
        new() { ExceptionCode = 0x80000004, Name = "SINGLE_STEP", Action = ExceptionAction.Break },
        new() { ExceptionCode = 0xC0000005, Name = "ACCESS_VIOLATION", Action = ExceptionAction.Continue },
        new() { ExceptionCode = 0xC000001D, Name = "ILLEGAL_INSTRUCTION (#UD)", Action = ExceptionAction.Break },
        new() { ExceptionCode = 0xC0000094, Name = "INTEGER_DIVIDE_BY_ZERO", Action = ExceptionAction.Continue },
        new() { ExceptionCode = 0xC0000096, Name = "PRIVILEGED_INSTRUCTION", Action = ExceptionAction.Break },
        new() { ExceptionCode = 0x80000001, Name = "GUARD_PAGE", Action = ExceptionAction.Break },
        new() { ExceptionCode = 0xC0000025, Name = "NONCONTINUABLE_EXCEPTION", Action = ExceptionAction.Continue }
    ];

    public ExceptionHandlingSettings Clone() => SettingsClone.Clone(this);
}

public sealed class SourceDebuggingSettings
{
    public bool Enabled { get; set; }

    public string SourceRoot { get; set; } = string.Empty;

    public string PdbRoot { get; set; } = string.Empty;

    public SourceDebuggingSettings Clone() => SettingsClone.Clone(this);
}

public sealed class HexMemoryViewSettings
{
    public string DisplayMode { get; set; } = "Hex";

    public int BytesPerRow { get; set; } = 16;

    public string TextEncoding { get; set; } = "ASCII";

    public static List<HexMemoryViewSettings> CreateDefaultList() =>
    [
        new(),
        new(),
        new(),
        new(),
        new(),
        new()
    ];

    public HexMemoryViewSettings Clone() => SettingsClone.Clone(this);
}

public sealed class ThemeColorProfile
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalColors { get; set; }

    public ThemeColorProfile Clone() => SettingsClone.Clone(this);
}

public enum McpTransportKind
{
    Stdio = 0,
    Http = 1,
    StreamableHttp = 2
}

public sealed class McpServerConfig
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public McpTransportKind Transport { get; set; }

    public string Command { get; set; } = string.Empty;

    public List<string> Args { get; set; } = [];

    public Dictionary<string, string> Env { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string WorkingDirectory { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int ConnectTimeoutSeconds { get; set; } = 30;

    public int CallTimeoutSeconds { get; set; } = 120;

    public List<string> DisabledTools { get; set; } = [];

    public bool Enabled { get; set; } = true;

    public McpServerConfig Clone() => SettingsClone.Clone(this);
}

public sealed class PluginStateSettings
{
    public bool IsEnabled { get; set; }

    public string LastVersion { get; set; } = string.Empty;

    public string LastError { get; set; } = string.Empty;

    public DateTimeOffset? LastLoadedAt { get; set; }

    public PluginStateSettings Clone() => SettingsClone.Clone(this);
}

public sealed class WindowLayoutData
{
    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public bool IsMaximized { get; set; }

    public WindowLayoutData Clone() => SettingsClone.Clone(this);
}

internal static class SettingsClone
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static T Clone<T>(T value)
        where T : class
    {
        string json = JsonSerializer.Serialize(value, Options);
        return JsonSerializer.Deserialize<T>(json, Options)
            ?? throw new InvalidOperationException($"Could not clone {typeof(T).Name}.");
    }
}
