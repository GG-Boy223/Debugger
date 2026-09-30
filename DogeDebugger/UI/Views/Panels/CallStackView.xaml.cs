using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DogeDebugger.UI.Views.Dialogs;

namespace DogeDebugger.UI.Views.Panels;

public partial class CallStackView : UserControl
{
    public CallStackView()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnCallStackRowDoubleClick(object sender, MouseButtonEventArgs eventArgs)
    {
        if (ViewModel is { } viewModel &&
            viewModel.NavigateToCallStackFrameCommand.CanExecute(null))
        {
            viewModel.NavigateToCallStackFrameCommand.Execute(null);
        }
    }

    private void OnCallStackKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.C &&
            (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            CopySelectedRows();
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Space && ViewModel is { } viewModel)
        {
            if (viewModel.UseSelectedCallStackRowAsReferenceCommand.CanExecute(null))
            {
                viewModel.UseSelectedCallStackRowAsReferenceCommand.Execute(null);
                eventArgs.Handled = true;
            }
        }
    }

    private void OnCopySelectedRowsClick(object sender, RoutedEventArgs eventArgs) =>
        CopySelectedRows();

    private void CopySelectedRows()
    {
        CallStackRow[] rows = CallStackGrid.SelectedItems.OfType<CallStackRow>().ToArray();
        if (rows.Length == 0 && ViewModel?.SelectedCallStackRow is { } selected)
        {
            rows = [selected];
        }

        MainViewModel.CopyCallStackRows(rows);
    }

    private void OnMaxStackBytesClick(object sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        AutoAssemblerAddressDialog dialog = new(
            "最大堆栈跟踪大小",
            "读取的栈字节数（512 - 1024）：",
            viewModel.MaxStackBytes.ToString(CultureInfo.InvariantCulture))
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true &&
            int.TryParse(
                dialog.Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int value))
        {
            viewModel.UpdateMaxStackBytes(value);
        }
    }
}
