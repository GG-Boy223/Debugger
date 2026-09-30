using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using DogeDebugger.Core.Modules;

namespace DogeDebugger.UI.Views.Panels;

public partial class ModuleListView : UserControl
{
    private ICollectionView? _view;

    public ModuleListView()
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
        _view = CollectionViewSource.GetDefaultView(ModuleGrid.ItemsSource);
        if (_view is null)
        {
            return;
        }

        _view.Filter = FilterModule;
        _view.Refresh();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs eventArgs)
    {
        _view?.Refresh();
    }

    private bool FilterModule(object value)
    {
        if (value is not ModuleDescriptor module)
        {
            return false;
        }

        string search = ModuleSearchBox.Text.Trim();
        return search.Length == 0 ||
               module.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               module.FilePath.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private async void OnModuleGridDoubleClick(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (DataContext is MainViewModel viewModel &&
            ModuleGrid.SelectedItem is ModuleDescriptor module)
        {
            ulong address = module.EntryPoint != 0
                ? module.EntryPoint
                : module.BaseAddress;
            await viewModel.NavigateToAddressAsync(address);
        }
    }
}
