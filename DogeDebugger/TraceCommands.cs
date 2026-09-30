using System.Windows.Input;

namespace DogeDebugger;

public static class TraceCommands
{
    public static readonly RoutedCommand Start =
        new("TraceStartCommand", typeof(MainWindow));

    public static readonly RoutedCommand Stop =
        new("TraceStopCommand", typeof(MainWindow));

    public static readonly RoutedCommand ShowResult =
        new("TraceShowResultCommand", typeof(MainWindow));
}
