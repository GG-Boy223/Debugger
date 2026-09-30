using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DogeDebugger.Core.Modules;

namespace DogeDebugger.UI.Views.Dialogs;

public partial class ExportSelectionWindow : Window
{
    private readonly IReadOnlyList<PeExport> _exports;

    public ExportSelectionWindow(
        IReadOnlyList<PeExport> exports,
        PeExport? selectedExport)
    {
        _exports = exports;
        InitializeComponent();
        ApplyFilter();
        if (selectedExport is not null)
        {
            ExportList.SelectedItem = _exports.FirstOrDefault(
                export => export.Ordinal == selectedExport.Ordinal &&
                          string.Equals(
                              export.Name,
                              selectedExport.Name,
                              StringComparison.Ordinal));
        }

        Loaded += (_, _) => SearchBox.Focus();
    }

    public PeExport? SelectedExport { get; private set; }

    private void OnSearchTextChanged(
        object sender,
        TextChangedEventArgs eventArgs)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string search = SearchBox?.Text.Trim() ?? string.Empty;
        ExportList.ItemsSource = string.IsNullOrWhiteSpace(search)
            ? _exports
            : _exports
                .Where(export => export.Name.Contains(
                    search,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
    }

    private void OnExportDoubleClick(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        CompleteSelection();
    }

    private void OnConfirmClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        CompleteSelection();
    }

    private void CompleteSelection()
    {
        if (ExportList.SelectedItem is not PeExport export)
        {
            return;
        }

        SelectedExport = export;
        DialogResult = true;
    }

    private void OnCancelClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        DialogResult = false;
    }
}
