using System.Windows.Input;

namespace DogeDebugger;

public static class ToolsCommands
{
    public static readonly RoutedCommand AllocateMemory =
        new("ToolsAllocateMemoryCommand", typeof(MainWindow));

    public static readonly RoutedCommand CreateThread =
        new("ToolsCreateThreadCommand", typeof(MainWindow));

    public static readonly RoutedCommand CallFunction =
        new("ToolsCallFunctionCommand", typeof(MainWindow));

    public static readonly RoutedCommand DllInject =
        new("ToolsDllInjectCommand", typeof(MainWindow));

    public static readonly RoutedCommand PointerScan =
        new("ToolsPointerScanCommand", typeof(MainWindow));

    public static readonly RoutedCommand SignatureSearch =
        new("ToolsSignatureSearchCommand", typeof(MainWindow));

    public static readonly RoutedCommand CacheManager =
        new("ToolsCacheManagerCommand", typeof(MainWindow));
}
