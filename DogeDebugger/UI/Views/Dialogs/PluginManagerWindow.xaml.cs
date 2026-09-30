using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using DogeDebugger.Core.Settings;
using DogeDebugger.Debugger.Plugins;
using DogeDebugger.PluginSdk;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace DogeDebugger.UI.Views.Dialogs;

public partial class PluginManagerWindow : FluentWindow
{
    private readonly PluginRuntime _runtime;
    private readonly SettingsStore _settings;
    private readonly string _pluginDirectory;
    private readonly ObservableCollection<PluginManagerRow> _rows = [];

    public PluginManagerWindow(
        PluginRuntime runtime,
        SettingsStore settings,
        string pluginDirectory)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);

        InitializeComponent();
        _runtime = runtime;
        _settings = settings;
        _pluginDirectory = Path.GetFullPath(pluginDirectory);
        PluginGrid.ItemsSource = _rows;
        RefreshRows();
    }

    public async Task LoadExternalPluginAsync()
    {
        OpenFileDialog dialog = new()
        {
            Title = "选择插件清单",
            Filter = "插件清单 (plugin.json)|plugin.json|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            PluginManifestReader reader = new();
            PluginDescriptor source = reader.Read(dialog.FileName);
            string targetDirectory = Path.Combine(
                _pluginDirectory,
                SanitizeDirectoryName(source.Manifest.Id));
            if (!Path.GetFullPath(source.Directory)
                    .Equals(Path.GetFullPath(targetDirectory), StringComparison.OrdinalIgnoreCase))
            {
                CopyDirectory(source.Directory, targetDirectory);
            }

            PluginDescriptor installed = reader.Read(
                Path.Combine(targetDirectory, "plugin.json"));
            PluginStateSettings state = GetOrCreateState(installed);
            state.IsEnabled = true;
            state.LastVersion = installed.Manifest.Version;
            state.LastError = string.Empty;
            await _runtime.LoadPluginAsync(installed);
            state.LastLoadedAt = DateTimeOffset.Now;
            _settings.Save();
            RefreshRows();
            SelectRow(installed.Manifest.Id);
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                this,
                exception.Message,
                "加载插件失败",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs eventArgs) => RefreshRows();

    private async void OnLoadExternalClick(object sender, RoutedEventArgs eventArgs) =>
        await LoadExternalPluginAsync();

    private async void OnLoadSelectedClick(object sender, RoutedEventArgs eventArgs)
    {
        if (PluginGrid.SelectedItem is not PluginManagerRow row)
        {
            return;
        }

        await SetEnabledAsync(row, enabled: true);
    }

    private async void OnToggleSelectedClick(object sender, RoutedEventArgs eventArgs)
    {
        if (PluginGrid.SelectedItem is not PluginManagerRow row)
        {
            return;
        }

        await SetEnabledAsync(row, !row.IsEnabled);
    }

    private async void OnUnloadSelectedClick(object sender, RoutedEventArgs eventArgs)
    {
        if (PluginGrid.SelectedItem is not PluginManagerRow row)
        {
            return;
        }

        try
        {
            await _runtime.UnloadPluginAsync(row.Id);
            row.Status = "已卸载";
            RefreshRows();
            SelectRow(row.Id);
        }
        catch (Exception exception)
        {
            row.Error = exception.Message;
        }
    }

    private void OnRemoveSelectedClick(object sender, RoutedEventArgs eventArgs)
    {
        if (PluginGrid.SelectedItem is not PluginManagerRow row)
        {
            return;
        }

        System.Windows.MessageBoxResult result = System.Windows.MessageBox.Show(
            this,
            $"确定删除插件“{row.Name}”及其数据目录吗？",
            "删除插件",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (result != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (_runtime.IsLoaded(row.Id))
            {
                _runtime.UnloadPluginAsync(row.Id).GetAwaiter().GetResult();
            }

            string pluginDirectory = Path.GetFullPath(row.Descriptor.Directory);
            if (IsWithinDirectory(_pluginDirectory, pluginDirectory))
            {
                Directory.Delete(pluginDirectory, recursive: true);
            }

            _settings.Current.PluginStates.Remove(row.Id);
            _settings.Save();
            RefreshRows();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                this,
                exception.Message,
                "删除插件失败",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs eventArgs) => Close();

    private async Task SetEnabledAsync(PluginManagerRow row, bool enabled)
    {
        try
        {
            PluginStateSettings state = GetOrCreateState(row.Descriptor);
            state.IsEnabled = enabled;
            state.LastVersion = row.Descriptor.Manifest.Version;
            state.LastError = string.Empty;
            if (enabled)
            {
                await _runtime.LoadPluginAsync(row.Descriptor);
                state.LastLoadedAt = DateTimeOffset.Now;
            }
            else
            {
                await _runtime.UnloadPluginAsync(row.Id);
            }

            _settings.Save();
            RefreshRows();
            SelectRow(row.Id);
        }
        catch (Exception exception)
        {
            row.Error = exception.Message;
            PluginStateSettings state = GetOrCreateState(row.Descriptor);
            state.LastError = exception.Message;
            _settings.Save();
            System.Windows.MessageBox.Show(
                this,
                exception.Message,
                enabled ? "启用插件失败" : "禁用插件失败",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private void RefreshRows()
    {
        string selectedId = (PluginGrid.SelectedItem as PluginManagerRow)?.Id ?? string.Empty;
        _rows.Clear();
        foreach (PluginDescriptor descriptor in _runtime.DiscoverPlugins(_pluginDirectory))
        {
            PluginStateSettings state = GetOrCreateState(descriptor);
            _rows.Add(new PluginManagerRow
            {
                Descriptor = descriptor,
                IsEnabled = state.IsEnabled,
                Status = _runtime.IsLoaded(descriptor.Manifest.Id)
                    ? "已加载"
                    : state.IsEnabled
                        ? "已启用（未加载）"
                        : "已禁用",
                Error = state.LastError
            });
        }

        SummaryText.Text = $"发现 {_rows.Count} 个插件，已加载 {_runtime.Plugins.Count} 个。";
        if (!string.IsNullOrWhiteSpace(selectedId))
        {
            SelectRow(selectedId);
        }
    }

    private PluginStateSettings GetOrCreateState(PluginDescriptor descriptor)
    {
        if (!_settings.Current.PluginStates.TryGetValue(
                descriptor.Manifest.Id,
                out PluginStateSettings? state))
        {
            state = new PluginStateSettings
            {
                IsEnabled = false,
                LastVersion = descriptor.Manifest.Version
            };
            _settings.Current.PluginStates.Add(descriptor.Manifest.Id, state);
        }

        if (!string.Equals(
                state.LastVersion,
                descriptor.Manifest.Version,
                StringComparison.Ordinal))
        {
            state.LastVersion = descriptor.Manifest.Version;
            state.LastError = string.Empty;
            _settings.Save();
        }

        return state;
    }

    private void SelectRow(string pluginId)
    {
        PluginManagerRow? row = _rows.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (row is not null)
        {
            PluginGrid.SelectedItem = row;
            PluginGrid.ScrollIntoView(row);
        }
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        string source = Path.GetFullPath(sourceDirectory);
        string target = Path.GetFullPath(targetDirectory);
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source))
        {
            string destination = Path.Combine(target, Path.GetFileName(file));
            File.Copy(file, destination, overwrite: true);
        }

        foreach (string directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    private static string SanitizeDirectoryName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character =>
            invalid.Contains(character) ? '_' : character).ToArray());
    }

    private static bool IsWithinDirectory(string directory, string path)
    {
        string relative = Path.GetRelativePath(directory, path);
        return !relative.StartsWith("..", StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }
}

public sealed class PluginManagerRow
{
    public required PluginDescriptor Descriptor { get; init; }

    public string Name => Descriptor.Manifest.Name;

    public string Id => Descriptor.Manifest.Id;

    public string Version => Descriptor.Manifest.Version;

    public string PluginType => Descriptor.Kind.ToString();

    public string Description => Descriptor.Manifest.Description;

    public bool IsEnabled { get; init; }

    public string Status { get; set; } = string.Empty;

    public string Error { get; set; } = string.Empty;
}
