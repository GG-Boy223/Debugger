using Wpf.Ui.Controls;

namespace DogeDebugger.UI.ViewModels;

public static class PanelCatalog
{
    public static readonly IReadOnlyList<PanelDescriptor> Panels =
    [
        new("MemorySearchPanel", "内存搜索", PanelGroup.Debugging, Icon: (SymbolRegular)63120),
        new("DisassemblyPanel", "反汇编", PanelGroup.Debugging, Icon: (SymbolRegular)62300),
        new("CrossRefPanel", "交叉引用", PanelGroup.Debugging, Icon: (SymbolRegular)62693),
        new("SourceDebugPanel", "源码调试", PanelGroup.Debugging, CreatedOnDemand: true, Icon: (SymbolRegular)62192),
        new("NotesPanel", "笔记", PanelGroup.Debugging, Icon: (SymbolRegular)62839),
        new("BreakpointPanel", "断点列表", PanelGroup.Debugging, Icon: (SymbolRegular)63074),
        new("TraceResultPanel", "追踪结果", PanelGroup.Debugging, CreatedOnDemand: true, Icon: (SymbolRegular)63525),
        new("MemoryMapPanel", "内存布局", PanelGroup.SearchAndAnalysis, Icon: (SymbolRegular)62766),
        new("RttiPanel", "RTTI", PanelGroup.SearchAndAnalysis, Icon: (SymbolRegular)62262),
        new("StringRefPanel", "字符串引用", PanelGroup.SearchAndAnalysis, Icon: (SymbolRegular)63489),
        new("ThreadListPanel", "线程列表", PanelGroup.SearchAndAnalysis, Icon: (SymbolRegular)983504),
        new("HandleListPanel", "句柄表", PanelGroup.SearchAndAnalysis, Icon: (SymbolRegular)62647),
        new("ExceptionHandlerPanel", "异常处理", PanelGroup.SearchAndAnalysis, Icon: (SymbolRegular)63594),
        new("AutoAssemblerPanel", "Auto Assembler", PanelGroup.Scripting, Icon: (SymbolRegular)63681),
        new("LuaScriptPanel", "Lua 脚本", PanelGroup.Scripting, Icon: (SymbolRegular)983610),
        new("CustomSymbolPanel", "自定义符号", PanelGroup.Information, Icon: (SymbolRegular)63357),
        new("ModuleListPanel", "模块", PanelGroup.Information, Icon: (SymbolRegular)57788),
        new("LogPanel", "日志", PanelGroup.Information, Icon: (SymbolRegular)60634),
        new("CommentPanel", "注释", PanelGroup.Information, Icon: (SymbolRegular)62208),
        new("MonoExplorerPanel", "Unity 扩展", PanelGroup.Extensions, Icon: (SymbolRegular)62545),
        new("UnrealExplorerPanel", "UnrealEngine 扩展", PanelGroup.Extensions, Icon: (SymbolRegular)62225)
    ];

    private static readonly Dictionary<string, PanelDescriptor> ByContentId =
        Panels.ToDictionary(
            static panel => panel.ContentId,
            StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(string? contentId, out PanelDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(contentId))
        {
            descriptor = null!;
            return false;
        }

        return ByContentId.TryGetValue(contentId, out descriptor!);
    }

    public static PanelGroup ResolveGroup(string? contentId, PanelGroup fallback)
    {
        return TryGet(contentId, out PanelDescriptor descriptor)
            ? descriptor.Group
            : fallback;
    }

    public static IEnumerable<PanelDescriptor> ForGroup(PanelGroup group)
    {
        return Panels.Where(panel => panel.Group == group);
    }
}
