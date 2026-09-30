using DogeDebugger.Core.Search;

namespace DogeDebugger.UI.ViewModels.Panels;

public sealed record ValueTypeOption(string DisplayName, MemoryValueKind Value)
{
    public override string ToString() => DisplayName;
}
