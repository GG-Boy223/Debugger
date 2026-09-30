using System.Windows.Input;

namespace DogeDebugger;

public static class HelpCommands
{
    public static readonly RoutedCommand About =
        new("HelpAboutCommand", typeof(MainWindow));

    public static readonly RoutedCommand CheatSheet =
        new("HelpCheatSheetCommand", typeof(MainWindow));
}
