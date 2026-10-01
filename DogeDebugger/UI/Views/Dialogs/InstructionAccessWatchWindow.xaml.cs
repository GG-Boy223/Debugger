using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DogeDebugger.Core.Disassembly;
using DogeDebugger.Debugger.Session;
using DogeDebugger.UI.ViewModels.Dialogs;

namespace DogeDebugger.UI.Views.Dialogs;

public partial class InstructionAccessWatchWindow : Window
{
    private readonly InstructionAccessWatchViewModel _viewModel;
    private readonly Action<ulong>? _navigateToDataView;
    private readonly Action<ulong>? _navigateToRtti;
    private readonly Action<InstructionAccessDisplayItem>? _parseMonoStruct;

    public InstructionAccessWatchWindow(
        DebuggerSession session,
        InstructionSnapshot instruction,
        InstructionMemoryOperand operand,
        int bitness,
        Action<ulong>? navigateToDataView = null,
        Action<ulong>? navigateToRtti = null,
        Action<InstructionAccessDisplayItem>? parseMonoStruct = null,
        Func<bool>? canParseMonoStruct = null)
    {
        InitializeComponent();
        _navigateToDataView = navigateToDataView;
        _navigateToRtti = navigateToRtti;
        _parseMonoStruct = parseMonoStruct;
        _viewModel = new InstructionAccessWatchViewModel(
            session,
            instruction,
            operand,
            bitness);
        _viewModel.CanParseMonoStructProvider = canParseMonoStruct;
        _viewModel.ShowError += OnShowError;
        _viewModel.CloseRequested += Close;
        _viewModel.NavigateToHexRequested += OnNavigateToHexRequested;
        _viewModel.OpenRttiRequested += OnOpenRttiRequested;
        _viewModel.ParseMonoStructRequested += OnParseMonoStructRequested;
        DataContext = _viewModel;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs) =>
        _viewModel.Start();

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        _viewModel.ShowError -= OnShowError;
        _viewModel.CloseRequested -= Close;
        _viewModel.NavigateToHexRequested -= OnNavigateToHexRequested;
        _viewModel.OpenRttiRequested -= OnOpenRttiRequested;
        _viewModel.ParseMonoStructRequested -= OnParseMonoStructRequested;
        _viewModel.Dispose();
    }

    private void OnNavigateToHexRequested(ulong address) =>
        _navigateToDataView?.Invoke(address);

    private void OnOpenRttiRequested(ulong address) =>
        _navigateToRtti?.Invoke(address);

    private void OnParseMonoStructRequested(
        InstructionAccessDisplayItem item) =>
        _parseMonoStruct?.Invoke(item);

    private void OnShowError(string message) =>
        MessageBox.Show(
            this,
            message,
            "指令访问的地址",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

    private void OnRecordGridPreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (sender is not DataGrid dataGrid)
        {
            return;
        }

        DataGridRow? row = FindAncestor<DataGridRow>(
            eventArgs.OriginalSource as DependencyObject);
        if (row is null)
        {
            dataGrid.SelectedItem = null;
            return;
        }

        row.IsSelected = true;
        row.Focus();
        dataGrid.SelectedItem = row.Item;
        eventArgs.Handled = true;
    }

    private void OnRecordContextMenuOpened(
        object sender,
        RoutedEventArgs eventArgs) =>
        _viewModel.RefreshContextCommandState();

    private static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void OnCopyAddressClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (RecordGrid.SelectedItem is InstructionAccessDisplayItem item)
        {
            SetClipboard(item.AddressText);
        }
    }

    private void OnCopyValueClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (RecordGrid.SelectedItem is InstructionAccessDisplayItem item)
        {
            SetClipboard(item.ValueText);
        }
    }

    private void OnToggleValueFormatClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        _viewModel.IsValueHexDisplay = !_viewModel.IsValueHexDisplay;

    private void OnNavigateAddressClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (RecordGrid.SelectedItem is InstructionAccessDisplayItem item)
        {
            _navigateToDataView?.Invoke(item.Address);
        }
    }

    private static void SetClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
        }
    }
}
