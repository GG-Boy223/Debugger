namespace DogeDebugger.Core.Shortcuts;

public static class ShortcutSchemeCatalog
{
    public const string DefaultSchemeId = "x64dbg";

    public static IReadOnlyList<ShortcutScheme> BuiltIn { get; } =
    [
        new(
            DefaultSchemeId,
            "x64dbg",
            isBuiltIn: true,
            baseSchemeId: null,
            new Dictionary<string, KeyChord?>(StringComparer.OrdinalIgnoreCase),
            "移植自 x64dbg 的默认按键：F9 运行、F2 断点、F7/F8 步进、Ctrl+F9 执行到返回、F12 暂停、Ctrl+G 跳转、Space 汇编、Enter 跟随。"),
        CreateScheme(
            "cheat-engine",
            "Cheat Engine",
            "Cheat Engine 内存查看器的习惯：F5 断点、F7/F8/F9 步进与运行、Space 跟随、Backspace 返回。",
            ("debug.toggleBreakpoint", "F5"),
            ("debug.pause", "F6"),
            ("debug.stepOut", "Shift+F8"),
            ("debug.detach", "Shift+F5"),
            ("disasm.follow", "Space"),
            ("disasm.assemble", null),
            ("disasm.nav.back", "Backspace"),
            ("disasm.nav.forward", "Shift+Backspace"),
            ("disasm.stepHistory.back", "-"),
            ("disasm.stepHistory.forward", "+"),
            ("hex.back", "Backspace"),
            ("hex.forward", "Shift+Backspace"),
            ("modules.refresh", "Ctrl+F5"),
            ("threads.refresh", "Ctrl+F5"),
            ("handles.refresh", "Ctrl+F5"),
            ("memmap.refresh", "Ctrl+F5"),
            ("exceptions.refresh", "Ctrl+F5")),
        CreateScheme(
            "visual-studio",
            "Visual Studio",
            "Visual Studio 调试器的习惯：F5 继续、F9 断点、F10/F11 步进、Shift+F11 步出、Ctrl+F10 运行到光标。",
            ("debug.run", "F5"),
            ("debug.pause", "Ctrl+Alt+Pause"),
            ("debug.detach", "Shift+F5"),
            ("debug.restart", "Ctrl+Shift+F5"),
            ("debug.stepInto", "F11"),
            ("debug.stepOver", "F10"),
            ("debug.stepOut", "Shift+F11"),
            ("debug.runToCursor", "Ctrl+F10"),
            ("debug.toggleBreakpoint", "F9"),
            ("disasm.setOrigin", "Ctrl+Shift+F10"),
            ("disasm.findReferences", "Shift+F12"),
            ("disasm.nav.back", "Ctrl+-"),
            ("disasm.nav.forward", "Ctrl+Shift+-"),
            ("view.breakpoints", "Ctrl+Alt+B"),
            ("view.callStack", "Ctrl+Alt+C"),
            ("view.modules", "Ctrl+Alt+U"),
            ("view.threads", "Ctrl+Alt+H"),
            ("view.registers", "Ctrl+Alt+G"),
            ("view.log", "Ctrl+Alt+O"),
            ("file.attach", "Ctrl+Alt+P"),
            ("breakpoints.clearAll", "Ctrl+Shift+F9"),
            ("panel.close", "Ctrl+F4"),
            ("modules.refresh", "Ctrl+F5"),
            ("threads.refresh", "Ctrl+F5"),
            ("handles.refresh", "Ctrl+F5"),
            ("memmap.refresh", "Ctrl+F5"),
            ("exceptions.refresh", "Ctrl+F5")),
        CreateScheme(
            "ollydbg",
            "OllyDbg",
            "OllyDbg 的习惯，与 x64dbg 基本一致：Alt+F2 关闭、Alt+H 句柄、Ctrl+A 分析。",
            ("debug.detach", "Alt+F2"),
            ("file.attach", null),
            ("view.handles", "Alt+H"),
            ("disasm.analyzeFunction", "Ctrl+A"))
    ];

    public static ShortcutScheme Default => BuiltIn[0];

    public static ShortcutScheme? Find(string? id)
    {
        return id is null
            ? null
            : BuiltIn.FirstOrDefault(
                scheme => string.Equals(
                    scheme.Id,
                    id,
                    StringComparison.OrdinalIgnoreCase));
    }

    private static ShortcutScheme CreateScheme(
        string id,
        string name,
        string description,
        params (string Id, string? Gesture)[] entries)
    {
        Dictionary<string, KeyChord?> delta =
            new(StringComparer.OrdinalIgnoreCase);
        foreach ((string actionId, string? gesture) in entries)
        {
            delta[actionId] = string.IsNullOrWhiteSpace(gesture)
                ? null
                : KeyChord.Parse(gesture);
        }

        return new ShortcutScheme(
            id,
            name,
            isBuiltIn: true,
            baseSchemeId: null,
            delta,
            description);
    }
}
