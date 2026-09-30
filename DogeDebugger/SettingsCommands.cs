using System.Windows.Input;

namespace DogeDebugger;

public static class SettingsCommands
{
    public static readonly RoutedCommand Open =
        new("OpenSettingsCommand", typeof(MainWindow));

    public static readonly RoutedCommand Shortcuts =
        new("OpenShortcutSettingsCommand", typeof(MainWindow));

    public static readonly RoutedCommand ResetLayout =
        new("ResetLayoutCommand", typeof(MainWindow));
}
