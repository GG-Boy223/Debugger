using System.Text.Json;
using System.Text.Json.Serialization;

namespace DogeDebugger.Core.Settings;

public sealed class AppSettings
{
    public bool AskClearListOnNewProcess { get; set; } = true;

    public string SymbolServerUrl { get; set; } = "http://msdl.dogecodex.com/download/symbols";

    public string MonoImageDumpExportDirectory { get; set; } = string.Empty;

    public int ValueLockIntervalMs { get; set; } = 10;

    public int SavedAddressRefreshIntervalMs { get; set; } = 100;

    public bool AddressListSortOnClick { get; set; } = true;

    public bool AddressListShowAbsoluteAddresses { get; set; } = true;

    public double AddressListLockColumnWidth { get; set; } = 44;

    public double AddressListDescriptionColumnWidth { get; set; } = 220;

    public double AddressListAddressColumnWidth { get; set; } = 170;

    public double AddressListTypeColumnWidth { get; set; } = 110;

    public bool SearchMemPrivate { get; set; } = true;

    public bool SearchMemImage { get; set; } = true;

    public bool SearchMemMapped { get; set; }

    public string MemorySearchStringEncoding { get; set; } = "UTF8";

    public int MemorySearchCustomCodePage { get; set; } = 936;

    public bool MemorySearchIgnoreCase { get; set; }

    public bool SaveWindowLayout { get; set; } = true;

    public bool ShowScriptConsole { get; set; }

    public int ActivityBarPosition { get; set; }

    public int LastActiveGroup { get; set; } = 1;

    public Dictionary<string, int> PanelGroupOverrides { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public int LayoutVersion { get; set; } = 5_000_004;

    public int IgnoredLayoutVersion { get; set; }

    public int LastCheckAppVersion { get; set; } = 5_000_000;

    public DateTime LastCacheCheckDate { get; set; } = DateTime.UtcNow;

    public List<HexMemoryViewSettings> HexMemoryViews { get; set; } =
        HexMemoryViewSettings.CreateDefaultList();

    public int HexViewRefreshIntervalMs { get; set; } = 100;

    public string GlobalNotes { get; set; } = string.Empty;

    public string ThemeMode { get; set; } = "Light";

    public ThemeColorProfile? CustomColors { get; set; }

    public string CodeFontFamily { get; set; } = "Consolas";

    public int CodeFontSize { get; set; } = 14;

    public string UiFontFamily { get; set; } = "Microsoft YaHei UI";

    public int UiFontSize { get; set; } = 12;

    public int AgentBackendKind { get; set; }

    public string AiApiUrl { get; set; } = "https://api.openai.com";

    public string AiApiKey { get; set; } = string.Empty;

    public string AiDefaultModel { get; set; } = string.Empty;

    public Dictionary<string, long> AiModelContextWindows { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public double AiTemperature { get; set; } = 0.7;

    public string AiSystemPrompt { get; set; } =
        "你是一个专业的逆向工程和汇编语言分析助手。用户会提供 x86/x64 反汇编代码，请分析其功能、调用约定、关键逻辑，并给出清晰的解释。";

    public string AiReasoningEffort { get; set; } = string.Empty;

    public bool AiSendOnEnter { get; set; } = true;

    public WindowLayoutData? AiChatWindowBounds { get; set; }

    public bool AiChatHistorySidebarExpanded { get; set; } = true;

    public string? AiChatLastSessionId { get; set; }

    public bool AiChatStreamingMarkdown { get; set; } = true;

    public bool AiReadOnlyMode { get; set; }

    public int AiAgentMode { get; set; }

    public bool AiAutoContinueOnLength { get; set; } = true;

    public bool AiShowReasoning { get; set; } = true;

    public bool AiIncludeBuiltInGuidelines { get; set; } = true;

    public bool HasShownAiAssistantGuide { get; set; }

    public string ClaudeCodePath { get; set; } = string.Empty;

    public string ClaudeCodeWorkingDirectory { get; set; } = string.Empty;

    public string ClaudeCodePermissionMode { get; set; } = "default";

    public bool UseClaudeDangerouslySkipPermissions { get; set; }

    public bool UseClaudeCodeCustomApi { get; set; } = true;

    public string ClaudeCodeApiUrl { get; set; } = string.Empty;

    public string ClaudeCodeApiKey { get; set; } = string.Empty;

    public string CodexPath { get; set; } = string.Empty;

    public string CodexWorkingDirectory { get; set; } = string.Empty;

    public bool UseCodexDangerouslyBypassApprovalsAndSandbox { get; set; }

    public string CodexHomeDirectory { get; set; } = string.Empty;

    public string CodexSelectedModel { get; set; } = string.Empty;

    public string CodexReasoningEffort { get; set; } = string.Empty;

    public bool UseCodexCustomApi { get; set; } = true;

    public string CodexApiUrl { get; set; } = string.Empty;

    public string CodexApiKey { get; set; } = string.Empty;

    public bool AgentToolsEnabled { get; set; } = true;

    public int AgentToolsApprovalMode { get; set; } = 1;

    public string AgentToolsWorkingDirectory { get; set; } = string.Empty;

    public string AgentToolsBashPath { get; set; } = string.Empty;

    public bool AiTodoOverlayExpanded { get; set; } = true;

    public int AgentToolsShellTimeoutSeconds { get; set; } = 120;

    public bool McpEnabled { get; set; } = true;

    public string McpListenAddress { get; set; } = "127.0.0.1";

    public int McpPort { get; set; } = 55556;

    public bool McpPrivacyProtectionEnabled { get; set; }

    public int McpOutputProfile { get; set; } = 2;

    public List<string> McpDisabledToolCategories { get; set; } = [];

    public bool ExternalMcpEnabled { get; set; } = true;

    public List<McpServerConfig> McpServers { get; set; } = [];

    public Dictionary<string, PluginStateSettings> PluginStates { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public string LoginUsername { get; set; } = string.Empty;

    public string LoginPassword { get; set; } = string.Empty;

    public bool RememberPassword { get; set; }

    public bool AutoLogin { get; set; }

    public int? DebugKeybindingPreset { get; set; }

    [JsonIgnore]
    public Dictionary<string, object>? GlobalHotkeys { get; set; }

    public WindowLayoutData? SettingsWindowLayout { get; set; }

    public bool DeleteBreakpointOnToggle { get; set; }

    public int DisassemblyBytesStyle { get; set; }

    public bool RestrictStepToCurrentThread { get; set; }

    public int SavedBreakpointRestoreMode { get; set; }

    public bool AutoEnableSavedBreakpointsOnLaunch { get; set; } = true;

    public string AutoAttachProcessNames { get; set; } = string.Empty;

    public bool AutoAttachAllowWhenProcessSelected { get; set; }

    public int AutoAttachIntervalMs { get; set; } = 500;

    public int AutoAttachSavedAddressMode { get; set; }

    public SourceDebuggingSettings SourceDebugging { get; set; } = new();

    public DebugEventSettings DebugEvents { get; set; } = new();

    public ExceptionHandlingSettings ExceptionHandling { get; set; } = new();

    public WindowLayoutData? WindowLayout { get; set; }

    public string? MainDockLayout { get; set; }

    public string? DisasmDockLayout { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }

    public AppSettings Clone()
    {
        string json = JsonSerializer.Serialize(this, SettingsJson.Options);
        return JsonSerializer.Deserialize<AppSettings>(json, SettingsJson.Options)
            ?? throw new InvalidOperationException("Could not clone application settings.");
    }
}

public static class SettingsJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
