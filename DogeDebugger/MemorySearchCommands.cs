using System.Windows.Input;

namespace DogeDebugger;

public static class MemorySearchCommands
{
    public static readonly RoutedCommand FirstScan =
        new("MemorySearchFirstScanCommand", typeof(MainWindow));

    public static readonly RoutedCommand NextScan =
        new("MemorySearchNextScanCommand", typeof(MainWindow));

    public static readonly RoutedCommand NewTab =
        new("MemorySearchNewTabCommand", typeof(MainWindow));
}
