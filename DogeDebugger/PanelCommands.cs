using System.Windows.Input;

namespace DogeDebugger;

public static class PanelCommands
{
    public static readonly RoutedCommand Next =
        new("PanelNextCommand", typeof(MainWindow));

    public static readonly RoutedCommand Previous =
        new("PanelPreviousCommand", typeof(MainWindow));

    public static readonly RoutedCommand Close =
        new("PanelCloseCommand", typeof(MainWindow));
}
