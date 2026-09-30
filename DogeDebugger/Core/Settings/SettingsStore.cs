using System.IO;
using System.Text;
using System.Text.Json;

namespace DogeDebugger.Core.Settings;

public sealed class SettingsStore
{
    private readonly object _sync = new();
    private readonly string _path;

    public SettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppContext.BaseDirectory, "settings.json");
        Current = LoadCore();
    }

    public AppSettings Current { get; private set; }

    public event Action<AppSettings>? Changed;

    public AppSettings Reload()
    {
        lock (_sync)
        {
            Current = LoadCore();
            return Current;
        }
    }

    public void Replace(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_sync)
        {
            Current = Normalize(settings);
            SaveCore(Current);
        }

        Changed?.Invoke(Current);
    }

    public void Save()
    {
        lock (_sync)
        {
            SaveCore(Current);
        }
    }

    private AppSettings LoadCore()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return Normalize(new AppSettings());
            }

            string json = File.ReadAllText(_path, Encoding.UTF8);
            AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json, SettingsJson.Options);
            return Normalize(settings ?? new AppSettings());
        }
        catch (IOException)
        {
            return Normalize(new AppSettings());
        }
        catch (UnauthorizedAccessException)
        {
            return Normalize(new AppSettings());
        }
        catch (JsonException)
        {
            return Normalize(new AppSettings());
        }
    }

    private void SaveCore(AppSettings settings)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = _path + ".tmp";
        string json = JsonSerializer.Serialize(settings, SettingsJson.Options);
        File.WriteAllText(temporaryPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (File.Exists(_path))
        {
            File.Replace(temporaryPath, _path, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temporaryPath, _path);
        }
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.SourceDebugging ??= new SourceDebuggingSettings();
        settings.DebugEvents ??= new DebugEventSettings();
        settings.ExceptionHandling ??= new ExceptionHandlingSettings();
        settings.ExceptionHandling.CustomRules ??= [];
        settings.HexMemoryViews ??= HexMemoryViewSettings.CreateDefaultList();
        settings.PanelGroupOverrides ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        settings.AiModelContextWindows ??=
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        settings.McpServers ??= [];
        settings.McpDisabledToolCategories ??= [];
        settings.PluginStates ??=
            new Dictionary<string, PluginStateSettings>(StringComparer.OrdinalIgnoreCase);
        settings.AutoAttachIntervalMs = Math.Clamp(settings.AutoAttachIntervalMs, 100, 60_000);
        settings.AgentToolsShellTimeoutSeconds = Math.Clamp(
            settings.AgentToolsShellTimeoutSeconds,
            5,
            3_600);
        settings.MemorySearchCustomCodePage =
            settings.MemorySearchCustomCodePage is > 0 and <= 65_535
                ? settings.MemorySearchCustomCodePage
                : 936;

        for (int index = 0; index < settings.McpServers.Count; index++)
        {
            McpServerConfig server = settings.McpServers[index];
            server.Id = string.IsNullOrWhiteSpace(server.Id)
                ? Guid.NewGuid().ToString("N")
                : server.Id;
            server.Name = string.IsNullOrWhiteSpace(server.Name)
                ? $"server-{server.Id[..Math.Min(6, server.Id.Length)]}"
                : server.Name;
            server.Args ??= [];
            server.Env ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            server.Headers ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            server.DisabledTools ??= [];
            server.ConnectTimeoutSeconds = Math.Clamp(server.ConnectTimeoutSeconds, 5, 600);
            server.CallTimeoutSeconds = Math.Clamp(server.CallTimeoutSeconds, 5, 3_600);
            settings.McpServers[index] = server;
        }

        for (int index = 0; index < settings.HexMemoryViews.Count; index++)
        {
            HexMemoryViewSettings view = settings.HexMemoryViews[index];
            view.BytesPerRow = Math.Clamp(view.BytesPerRow, 1, 64);
            view.DisplayMode = string.IsNullOrWhiteSpace(view.DisplayMode) ? "Hex" : view.DisplayMode;
            view.TextEncoding = string.IsNullOrWhiteSpace(view.TextEncoding)
                ? "ASCII"
                : view.TextEncoding;
            settings.HexMemoryViews[index] = view;
        }

        return settings;
    }
}
