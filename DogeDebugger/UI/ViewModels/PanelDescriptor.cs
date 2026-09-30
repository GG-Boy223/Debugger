using Wpf.Ui.Controls;

namespace DogeDebugger.UI.ViewModels;

public sealed record PanelDescriptor(
    string ContentId,
    string Title,
    PanelGroup Group,
    bool CanClose = false,
    bool CreatedOnDemand = false,
    SymbolRegular? Icon = null);
