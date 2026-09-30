namespace DogeDebugger.UI.ViewModels.Panels;

public sealed record ScanTypeOption(string DisplayName, MemoryScanType Value)
{
    public override string ToString() => DisplayName;
}
