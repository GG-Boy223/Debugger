using System.Windows;
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
        _settings = new SettingsStore();
        _session = new DebuggerSession();

        McpToolRegistry registry = new();
        new DebuggerMcpTools(_session).Register(registry);
        _pluginRuntime = new PluginRuntime(_session, registry);
        _mcpServer = new McpServer(registry);

        MainViewModel viewModel = new(_session, _settings, _mcpServer, _pluginRuntime);
        if (string.Equals(
                Environment.GetEnvironmentVariable("DOGEDEBUGGER_HEADLESS"),
                "1",
                StringComparison.Ordinal))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await viewModel.InitializeAsync().ConfigureAwait(true);
            return;
        }

        MainWindow window = new()
        {
            DataContext = viewModel
        };
        window.AttachPluginRuntime(_pluginRuntime);
        window.RestoreWindowLayout(viewModel.Settings);
        MainWindow = window;
        window.Show();

        await viewModel.InitializeAsync().ConfigureAwait(true);
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
