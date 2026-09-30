using System.Windows.Input;

namespace DogeDebugger;

public static class PluginCommands
{
    public static readonly RoutedUICommand Manage = new(
        "插件管理器",
        nameof(Manage),
        typeof(PluginCommands));

    public static readonly RoutedUICommand Load = new(
        "加载插件",
        nameof(Load),
        typeof(PluginCommands));
}
