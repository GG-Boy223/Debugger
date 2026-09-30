using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using DogeDebugger.Core.StringRef;

namespace DogeDebugger.UI.Views.Panels;

public partial class StringRefListView : UserControl
{
    private ICollectionView? _view;

    public StringRefListView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs) => AttachView();

    private void OnDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs eventArgs) =>
        AttachView();

    private void AttachView()
    {
        _view = CollectionViewSource.GetDefaultView(StringGrid.ItemsSource);
        if (_view is null)
        {
            return;
        }

        _view.Filter = FilterString;
        _view.Refresh();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs eventArgs)
    {
        _view?.Refresh();
    }

    private bool FilterString(object value)
    {
        if (value is not StringEntry entry)
        {
            return false;
        }

        string search = StringSearchBox.Text.Trim();
        return search.Length == 0 ||
               entry.Text.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               entry.AddressText.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private async void OnStringGridDoubleClick(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (DataContext is MainViewModel viewModel &&
            StringGrid.SelectedItem is StringEntry entry)
        {
            await viewModel.NavigateToAddressAsync(entry.Address);
        }
    }

    private async void OnReferenceGridDoubleClick(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (DataContext is MainViewModel viewModel &&
            sender is DataGrid { SelectedItem: ulong address })
        {
            await viewModel.NavigateToAddressAsync(address);
        }
    }
}
