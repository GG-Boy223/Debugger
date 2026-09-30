using System.Windows;
using System.Windows.Controls;

namespace DogeDebugger.UI.Views.Panels;

public partial class HexView : UserControl
{
    public HexView()
    {
        InitializeComponent();
        HexTabs.ItemsSource = Enumerable.Range(1, 6).ToArray();
        HexTabs.SelectedIndex = 0;
    }

    private void OnPreviousHexTab(object sender, RoutedEventArgs eventArgs)
    {
        if (HexTabs.Items.Count == 0)
        {
            return;
        }

        HexTabs.SelectedIndex =
            (HexTabs.SelectedIndex - 1 + HexTabs.Items.Count) %
            HexTabs.Items.Count;
    }

    private void OnNextHexTab(object sender, RoutedEventArgs eventArgs)
    {
        if (HexTabs.Items.Count == 0)
        {
            return;
        }

        HexTabs.SelectedIndex =
            (HexTabs.SelectedIndex + 1) % HexTabs.Items.Count;
    }
}
