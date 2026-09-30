using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DogeDebugger.Core.Threading;

namespace DogeDebugger.UI.Views.Panels;

public partial class ThreadListView : UserControl
{
    public ThreadListView()
    {
        InitializeComponent();
    }

    private async void OnThreadGridDoubleClick(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (DataContext is MainViewModel viewModel &&
            ThreadGrid.SelectedItem is ThreadDescriptor thread &&
            thread.StartAddress != 0)
        {
            await viewModel.NavigateToAddressAsync(thread.StartAddress);
        }
    }
}
