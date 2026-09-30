using System.Windows.Input;

namespace DogeDebugger;

public static class FileCommands
{
    public static readonly RoutedCommand OpenProcess =
        new("OpenProcessCommand", typeof(MainWindow));

    public static readonly RoutedCommand Attach =
        new("AttachToProcessCommand", typeof(MainWindow));

    public static readonly RoutedCommand Launch =
        new("LaunchDebugCommand", typeof(MainWindow));

    public static readonly RoutedCommand Exit =
        new("ExitApplicationCommand", typeof(MainWindow));
}
