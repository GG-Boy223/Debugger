using System.Windows;
using System.Windows.Media;
using System.IO;
using DogeDebugger.Core.Settings;
using DogeDebugger.Core.AI;
using DogeDebugger.Debugger.Plugins;
using DogeDebugger.Debugger.Session;

namespace DogeDebugger;

public partial class App : Application
{
    private SettingsStore? _settings;
    private DebuggerSession? _session;
    private McpServer? _mcpServer;
    private PluginRuntime? _pluginRuntime;
    private bool _shuttingDown;

    private async void OnStartup(object sender, StartupEventArgs eventArgs)
    {
        TraceStartup("OnStartup begin");
        _settings = new SettingsStore();
        TraceStartup("settings loaded");
        ApplyFontResources(_settings.Current);
        _settings.Changed += ApplyFontResources;
        _session = new DebuggerSession();

        McpToolRegistry registry = new();
        new DebuggerMcpTools(_session).Register(registry);
        TraceStartup("mcp tools registered");
        _pluginRuntime = new PluginRuntime(_session, registry);
        _mcpServer = new McpServer(registry);

        MainViewModel viewModel = new(_session, _settings, _mcpServer, _pluginRuntime);
        TraceStartup("view model constructed");
        if (string.Equals(
                Environment.GetEnvironmentVariable("DOGEDEBUGGER_HEADLESS"),
                "1",
                StringComparison.Ordinal))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            TraceStartup("headless initialize begin");
            await viewModel.InitializeAsync().ConfigureAwait(true);
            TraceStartup("headless initialize complete");
            return;
        }

        MainWindow window = new()
        {
            DataContext = viewModel
        };
        TraceStartup("main window constructed");
        window.AttachPluginRuntime(_pluginRuntime);
        window.RestoreWindowLayout(viewModel.Settings);
        MainWindow = window;
        window.Show();
        TraceStartup("main window shown");

        TraceStartup("view model initialize begin");
        await viewModel.InitializeAsync().ConfigureAwait(true);
        TraceStartup("view model initialized");
        TraceStartup($"mcp state: {viewModel.McpStateText}");
    }

    private static void TraceStartup(string message)
    {
        string? path = Environment.GetEnvironmentVariable("DOGEDEBUGGER_STARTUP_TRACE");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.AppendAllText(
                path,
                $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void ApplyFontResources(AppSettings settings)
    {
        FontFamily codeFont = new(
            string.Concat(settings.CodeFontFamily, ", Consolas, monospace"));
        FontFamily uiFont = new(
            string.Concat(settings.UiFontFamily, ", Segoe UI"));
        double codeFontSize = Math.Clamp(settings.CodeFontSize, 8, 48);
        double uiFontSize = Math.Clamp(settings.UiFontSize, 8, 32);

        Resources["AppCodeFontFamily"] = codeFont;
        Resources["AppCodeFontSize"] = codeFontSize;
        Resources["AppUiFontFamily"] = uiFont;
        Resources["AppUiFontSize"] = uiFontSize;

        // WPF UI controls resolve their own typography resources before the
        // inherited window values, so mirror the configured UI font there too.
        Resources["ControlContentThemeFontSize"] = uiFontSize;
        Resources["ControlFontSize"] = uiFontSize;
    }

    private async void OnExit(object sender, ExitEventArgs eventArgs)
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        if (_mcpServer is not null)
        {
            await _mcpServer.StopAsync().ConfigureAwait(true);
            await _mcpServer.DisposeAsync().ConfigureAwait(true);
        }

        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(true);
        }

        if (_pluginRuntime is not null)
        {
            await _pluginRuntime.DisposeAsync().ConfigureAwait(true);
        }

        _settings?.Save();
    }
}
