using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DogeDebugger.UI.Views.Dialogs;

namespace DogeDebugger.UI.Views.Panels;

public partial class RegisterView : UserControl
{
    public RegisterView()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private RegisterRow? SelectedRegister =>
        RegisterGrid.SelectedItem as RegisterRow;

    private void OnRegisterGridPreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.OriginalSource is not DependencyObject source)
        {
            return;
        }

        DataGridRow? row = ItemsControl.ContainerFromElement(
            RegisterGrid,
            source) as DataGridRow;
        if (row is not null)
        {
            RegisterGrid.SelectedItem = row.Item;
        }
    }

    private void OnRegisterContextMenuOpened(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        bool hasSelection = SelectedRegister is not null;
        NavigateDisassemblyMenu.IsEnabled = hasSelection;
        NavigateHexMenu.IsEnabled = hasSelection;
        ToggleRegisterBaseMenu.IsChecked = viewModel.RegisterDecimalDisplay;
        HighlightChangedMenu.IsChecked = viewModel.HighlightChangedRegisters;
    }

    private void OnEditRegisterValueClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel ||
            SelectedRegister is not { } register)
        {
            return;
        }

        TextInputDialog dialog = new(
            $"修改值 - {register.Name}",
            "输入新值（十六进制或十进制）：",
            $"0x{register.Value:X}")
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true)
        {
            viewModel.SetRegisterValue(register.Name, dialog.Value);
        }
    }

    private async void OnNavigateRegisterDisassemblyClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel ||
            SelectedRegister is not { } register)
        {
            return;
        }

        await viewModel.NavigateToAddressAsync(register.Value);
        if (Window.GetWindow(this) is MainWindow mainWindow)
        {
            mainWindow.ShowDisassemblyPanel();
        }
    }

    private async void OnNavigateRegisterHexClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel ||
            SelectedRegister is not { } register)
        {
            return;
        }

        await viewModel.NavigateToAddressAsync(register.Value);
        if (Window.GetWindow(this) is MainWindow mainWindow)
        {
            mainWindow.ShowHexPanel();
        }
    }

    private void OnCopyRegisterValueClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is { } viewModel &&
            SelectedRegister is { } register)
        {
            Copy(viewModel, register.ValueText);
        }
    }

    private void OnCopyRegisterNameClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is { } viewModel &&
            SelectedRegister is { } register)
        {
            Copy(viewModel, register.Name);
        }
    }

    private void OnToggleRegisterBaseClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        ViewModel?.ToggleRegisterDecimalDisplay();

    private void OnToggleHighlightChangedClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        ViewModel?.ToggleRegisterChangeHighlight();

    private static void Copy(MainViewModel viewModel, string text)
    {
        try
        {
            Clipboard.SetText(text);
            viewModel.StatusText = "已复制到剪贴板。";
        }
        catch (Exception exception)
        {
            viewModel.StatusText = exception.Message;
        }
    }
}
