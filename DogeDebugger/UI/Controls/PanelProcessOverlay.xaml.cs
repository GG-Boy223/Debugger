using System.Windows.Controls;

namespace DogeDebugger.UI.Controls;

/// <summary>
/// Shared "waiting for a target process" overlay used by the panels that only
/// become usable after a process is opened or attached.
/// </summary>
public partial class PanelProcessOverlay : UserControl
{
    public PanelProcessOverlay()
    {
        InitializeComponent();
    }
}
