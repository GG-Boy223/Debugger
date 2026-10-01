using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DogeDebugger.Debugger.Breakpoints;
using DogeDebugger.UI.Views.Dialogs;

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

    private void OnBreakpointContextMenuOpening(
        object sender,
        ContextMenuEventArgs eventArgs)
    {
        bool hasSelection =
            BreakpointGrid.SelectedItem is BreakpointEntry;
        EditConditionMenuItem.IsEnabled = hasSelection;
        ClearConditionMenuItem.IsEnabled =
            BreakpointGrid.SelectedItem is BreakpointEntry
            {
                Condition.Length: > 0
            };
    }

    private void OnBreakpointGridPreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.OriginalSource is not DependencyObject source)
        {
            return;
        }

        DataGridRow? row = ItemsControl.ContainerFromElement(
            BreakpointGrid,
            source) as DataGridRow;
        if (row is not null)
        {
            BreakpointGrid.SelectedItem = row.Item;
        }
    }

    private void OnToggleBreakpointClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (BreakpointGrid.SelectedItem is BreakpointEntry breakpoint &&
            DataContext is MainViewModel viewModel)
        {
            viewModel.ToggleBreakpointEnabled(breakpoint);
        }
    }

    private void OnEditConditionClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (BreakpointGrid.SelectedItem is not BreakpointEntry breakpoint ||
            DataContext is not MainViewModel viewModel)
        {
            return;
        }

        ConditionBreakpointDialog dialog = new(
            breakpoint.Address,
            breakpoint.Condition,
            viewModel.ValidateBreakpointCondition)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true)
        {
            viewModel.UpdateBreakpointCondition(
                breakpoint.Address,
                dialog.Condition);
        }
    }

    private void OnClearConditionClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (BreakpointGrid.SelectedItem is BreakpointEntry breakpoint &&
            DataContext is MainViewModel viewModel)
        {
            viewModel.ClearBreakpointCondition(breakpoint.Address);
        }
    }

    private void OnDeleteBreakpointClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (BreakpointGrid.SelectedItem is not BreakpointEntry breakpoint ||
            DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (breakpoint.Kind == BreakpointKind.Software)
        {
            viewModel.RemoveBreakpoint(breakpoint.Address);
        }
        else
        {
            viewModel.RemoveHardwareBreakpoint(breakpoint.Address);
        }
    }

    private void OnClearAllBreakpointsClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.ClearAllBreakpoints();
        }
    }
}
