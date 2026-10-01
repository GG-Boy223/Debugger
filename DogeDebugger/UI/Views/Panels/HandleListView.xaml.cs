using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using DogeDebugger.Core.Handles;

namespace DogeDebugger.UI.Views.Panels;

public partial class HandleListView : UserControl
{
    private ICollectionView? _view;

    public HandleListView()
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
        _view = CollectionViewSource.GetDefaultView(HandleGrid.ItemsSource);
        if (_view is null)
        {
            return;
        }

        _view.Filter = FilterHandle;
        _view.Refresh();
        UpdateCounts();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs eventArgs)
    {
        _view?.Refresh();
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        HandleTotalCountRun.Text = (HandleGrid.ItemsSource as System.Collections.IEnumerable)
            ?.Cast<object>()
            .Count()
            .ToString() ?? "0";
        HandleViewCountRun.Text = _view?.Cast<object>().Count().ToString() ?? "0";
    }

    private bool FilterHandle(object value)
    {
        if (value is not ProcessHandleEntry handle)
        {
            return false;
        }

        if (NamedOnlyCheck.IsChecked == true &&
            string.IsNullOrWhiteSpace(handle.Name))
        {
            return false;
        }

        string search = HandleSearchBox.Text.Trim();
        return search.Length == 0 ||
               handle.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               handle.TypeName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               handle.HandleValueText.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private async void OnHandleGridDoubleClick(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (DataContext is MainViewModel viewModel &&
            HandleGrid.SelectedItem is ProcessHandleEntry handle &&
            handle.ObjectAddress != 0)
        {
            await viewModel.NavigateToAddressAsync(handle.ObjectAddress);
        }
    }
}
