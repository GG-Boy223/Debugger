using System.Windows.Input;

namespace DogeDebugger;

public static class LuaCommands
{
    public static readonly RoutedCommand Run =
        new("LuaRunCommand", typeof(MainWindow));

    public static readonly RoutedCommand Stop =
        new("LuaStopCommand", typeof(MainWindow));
}
