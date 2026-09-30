using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DogeDebugger.Debugger.Breakpoints;

namespace DogeDebugger.UI.Views.Panels;

public partial class BreakpointListView : UserControl
{
    public BreakpointListView()
    {
        InitializeComponent();
    }

    private async void OnBreakpointGridDoubleClick(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        await NavigateToSelectedBreakpointAsync();
    }

    private async void OnGoToBreakpointClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        await NavigateToSelectedBreakpointAsync();
    }

    private async Task NavigateToSelectedBreakpointAsync()
    {
        if (DataContext is MainViewModel viewModel &&
            BreakpointGrid.SelectedItem is BreakpointEntry breakpoint)
        {
            await viewModel.NavigateToAddressAsync(breakpoint.Address);
        }
    }

    private void OnBreakpointEnabledClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (sender is CheckBox { DataContext: BreakpointEntry breakpoint } &&
            DataContext is MainViewModel viewModel)
        {
            viewModel.ToggleBreakpointEnabled(breakpoint);
            eventArgs.Handled = true;
        }
    }
}
