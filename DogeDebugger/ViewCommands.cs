using System.Windows.Input;

namespace DogeDebugger;

public static class ViewCommands
{
    public static readonly RoutedCommand MemorySearch =
        new("ViewMemorySearchCommand", typeof(MainWindow));

    public static readonly RoutedCommand Disassembly =
        new("ViewDisassemblyCommand", typeof(MainWindow));

    public static readonly RoutedCommand Hex =
        new("ViewHexCommand", typeof(MainWindow));

    public static readonly RoutedCommand Registers =
        new("ViewRegistersCommand", typeof(MainWindow));

    public static readonly RoutedCommand CallStack =
        new("ViewCallStackCommand", typeof(MainWindow));

    public static readonly RoutedCommand TraceResult =
        new("ViewTraceResultCommand", typeof(MainWindow));

    public static readonly RoutedCommand Modules =
        new("ViewModulesCommand", typeof(MainWindow));

    public static readonly RoutedCommand Threads =
        new("ViewThreadsCommand", typeof(MainWindow));

    public static readonly RoutedCommand MemoryMap =
        new("ViewMemoryMapCommand", typeof(MainWindow));

    public static readonly RoutedCommand Handles =
        new("ViewHandlesCommand", typeof(MainWindow));

    public static readonly RoutedCommand Breakpoints =
        new("ViewBreakpointsCommand", typeof(MainWindow));

    public static readonly RoutedCommand CrossReferences =
        new("ViewCrossReferencesCommand", typeof(MainWindow));

    public static readonly RoutedCommand SourceDebug =
        new("ViewSourceDebugCommand", typeof(MainWindow));

    public static readonly RoutedCommand StringReferences =
        new("ViewStringReferencesCommand", typeof(MainWindow));

    public static readonly RoutedCommand Rtti =
        new("ViewRttiCommand", typeof(MainWindow));

    public static readonly RoutedCommand Unity =
        new("ViewUnityCommand", typeof(MainWindow));

    public static readonly RoutedCommand UnrealEngine =
        new("ViewUnrealEngineCommand", typeof(MainWindow));

    public static readonly RoutedCommand Exceptions =
        new("ViewExceptionsCommand", typeof(MainWindow));

    public static readonly RoutedCommand Notes =
        new("ViewNotesCommand", typeof(MainWindow));

    public static readonly RoutedCommand Comments =
        new("ViewCommentsCommand", typeof(MainWindow));

    public static readonly RoutedCommand CustomSymbols =
        new("ViewCustomSymbolsCommand", typeof(MainWindow));

    public static readonly RoutedCommand Log =
        new("ViewLogCommand", typeof(MainWindow));

    public static readonly RoutedCommand AutoAssembler =
        new("ViewAutoAssemblerCommand", typeof(MainWindow));

    public static readonly RoutedCommand LuaScript =
        new("ViewLuaScriptCommand", typeof(MainWindow));
}
