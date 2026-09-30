using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using DogeDebugger.Plugins.UnrealEngine.Models;

namespace DogeDebugger.Plugins.UnrealEngine.UI;

public partial class UnrealExplorerView : UserControl
{
    private ICollectionView? _objectView;
    private ICollectionView? _typeView;

    public UnrealExplorerView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs eventArgs)
    {
        AttachViews();
    }

    private void OnDataContextChanged(
        object sender,
        System.Windows.DependencyPropertyChangedEventArgs eventArgs)
    {
        AttachViews();
    }

    private void AttachViews()
    {
        _objectView = CollectionViewSource.GetDefaultView(ObjectGrid.ItemsSource);
        _typeView = CollectionViewSource.GetDefaultView(TypeGrid.ItemsSource);
        if (_objectView is not null)
        {
            _objectView.Filter = FilterObject;
        }

        if (_typeView is not null)
        {
            _typeView.Filter = FilterType;
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs eventArgs)
    {
        _objectView?.Refresh();
        _typeView?.Refresh();
    }

    private bool FilterObject(object value)
    {
        if (value is not UnrealObjectInfo item)
        {
            return false;
        }

        string search = UnrealSearchBox.Text.Trim();
        return search.Length == 0 ||
               item.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               item.ClassName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               item.FullName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               item.AddressHex.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private bool FilterType(object value)
    {
        if (value is not UnrealTypeInfo item)
        {
            return false;
        }

        string search = UnrealSearchBox.Text.Trim();
        return search.Length == 0 ||
               item.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               item.ClassName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               item.FullName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               item.AddressHex.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void OnObjectGridDoubleClick(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (DataContext is UnrealExplorerViewModel viewModel &&
            viewModel.NavigateSelectedObjectCommand.CanExecute(null))
        {
            viewModel.NavigateSelectedObjectCommand.Execute(null);
        }
    }
}
