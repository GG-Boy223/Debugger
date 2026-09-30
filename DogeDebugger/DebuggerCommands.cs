using System.Windows.Input;

namespace DogeDebugger;

public static class DebuggerCommands
{
    public static readonly RoutedCommand Run =
        new("DebugRunCommand", typeof(MainWindow));

    public static readonly RoutedCommand Pause =
        new("DebugPauseCommand", typeof(MainWindow));

    public static readonly RoutedCommand PassException =
        new("DebugPassExceptionCommand", typeof(MainWindow));

    public static readonly RoutedCommand Detach =
        new("DebugStopCommand", typeof(MainWindow));

    public static readonly RoutedCommand StepInto =
        new("DebugStepIntoCommand", typeof(MainWindow));

    public static readonly RoutedCommand StepOver =
        new("DebugStepOverCommand", typeof(MainWindow));

    public static readonly RoutedCommand StepOut =
        new("DebugStepOutCommand", typeof(MainWindow));

    public static readonly RoutedCommand RunToCursor =
        new("DebugRunToCursorCommand", typeof(MainWindow));

    public static readonly RoutedCommand Restart =
        new("DebugRestartCommand", typeof(MainWindow));

    public static readonly RoutedCommand ToggleBreakpoint =
        new("ToggleBreakpointCommand", typeof(MainWindow));
}
