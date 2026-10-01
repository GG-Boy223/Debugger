using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DogeDebugger.UI.ViewModels.Panels;
using DogeDebugger.UI.Views.Dialogs;

namespace DogeDebugger.UI.Views.Panels;

public partial class MemorySearchView : UserControl
{
    public MemorySearchView()
    {
        InitializeComponent();
    }

    private void OnResultsMouseDoubleClick(object sender, MouseButtonEventArgs eventArgs)
    {
        if (DataContext is MemorySearchViewModel viewModel &&
            viewModel.AddSelectedToAddressListCommand.CanExecute(null))
        {
            viewModel.AddSelectedToAddressListCommand.Execute(null);
        }
    }

    private void OnAddSelectedToAddressListClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        ExecuteCommand(static viewModel => viewModel.AddSelectedToAddressListCommand);

    private void OnOpenSelectedResultClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        ExecuteCommand(static viewModel => viewModel.OpenSelectedResultCommand);

    private void OnCopySelectedAddressClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        ExecuteCommand(static viewModel => viewModel.CopySelectedAddressCommand);

    private void OnClearSavedAddressesClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        ExecuteCommand(static viewModel => viewModel.ClearSavedAddressesCommand);

    private void OnOpenSelectedSavedAddressClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        ExecuteCommand(static viewModel => viewModel.OpenSelectedSavedAddressCommand);

    private void OnToggleSelectedSavedAddressClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        ExecuteCommand(static viewModel => viewModel.ToggleSelectedSavedAddressFrozenCommand);

    private void OnCopySelectedSavedAddressClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        ExecuteCommand(static viewModel => viewModel.CopySelectedSavedAddressCommand);

    private void OnDeleteSelectedSavedAddressClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        ExecuteCommand(static viewModel => viewModel.DeleteSelectedSavedAddressCommand);

    private void OnSavedAddressPreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        DependencyObject? source = eventArgs.OriginalSource as DependencyObject;
        while (source is not null and not ListBoxItem)
        {
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        if (source is ListBoxItem item)
        {
            item.IsSelected = true;
        }
    }

    private void OnAddAddressManuallyClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (DataContext is not MemorySearchViewModel viewModel)
        {
            return;
        }

        ManualAddressWindow dialog = new(viewModel.ReadValuePreview)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            viewModel.AddAddressFromSource(dialog.Result);
        }
    }

    private void ExecuteCommand(
        Func<MemorySearchViewModel, System.Windows.Input.ICommand> commandFactory)
    {
        if (DataContext is not MemorySearchViewModel viewModel)
        {
            return;
        }

        System.Windows.Input.ICommand command = commandFactory(viewModel);
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }
}
