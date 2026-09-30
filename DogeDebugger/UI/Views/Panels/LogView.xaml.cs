using System.Windows;
using System.Windows.Controls;

namespace DogeDebugger.UI.Views.Panels;

public partial class LogView : UserControl
{
    public LogView()
    {
        InitializeComponent();
    }

    private void OnClearLogClick(object sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.LogMessages.Clear();
        }
    }
}
