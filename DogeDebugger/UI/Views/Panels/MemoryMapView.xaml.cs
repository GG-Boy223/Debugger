using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using DogeDebugger.Core.Memory;

namespace DogeDebugger.UI.Views.Panels;

public partial class MemoryMapView : UserControl
{
    private ICollectionView? _view;

    public MemoryMapView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        RegionGrid.SizeChanged += OnRegionGridSizeChanged;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        AttachView();
        ApplyColumnWidths();
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            ReportColumnWidths);
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(ApplyColumnWidths));
    }

    private void OnDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        AttachView();
    }

    private void AttachView()
    {
        _view = CollectionViewSource.GetDefaultView(RegionGrid.ItemsSource);
        if (_view is null)
        {
            return;
        }

        _view.Filter = FilterRegion;
        ApplyGrouping();
        _view.Refresh();
    }

    private void ApplyColumnWidths()
    {
        double[] widths = [160, 130, 80, 140, 80, 120, 140, 100];
        for (int index = 0;
             index < widths.Length && index < RegionGrid.Columns.Count;
             index++)
        {
            DataGridColumn column = RegionGrid.Columns[index];
            column.MinWidth = widths[index];
            column.Width = new DataGridLength(widths[index]);
        }

        if (RegionGrid.Columns.Count > widths.Length)
        {
            DataGridColumn column = RegionGrid.Columns[^1];
            column.MinWidth = 100;
            column.Width =
                new DataGridLength(
                    1,
                    DataGridLengthUnitType.Star);
        }
    }

    private void OnRegionGridSizeChanged(
        object sender,
        SizeChangedEventArgs eventArgs)
    {
        if (eventArgs.NewSize.Width > 100)
        {
            ApplyColumnWidths();
        }
    }

    private void OnFilterChanged(object sender, RoutedEventArgs eventArgs)
    {
        ApplyGrouping();
        _view?.Refresh();
    }

    private void ApplyGrouping()
    {
        if (_view is null)
        {
            return;
        }

        using (_view.DeferRefresh())
        {
            _view.GroupDescriptions.Clear();
            if (GroupByModuleCheck.IsChecked == true)
            {
                _view.GroupDescriptions.Add(
                    new PropertyGroupDescription(nameof(MemoryRegionInfo.ModuleName)));
            }
        }
    }

    private bool FilterRegion(object value)
    {
        if (value is not MemoryRegionInfo region)
        {
            return false;
        }

        bool stateMatches = region.State switch
        {
            0x1000 => CommitCheck.IsChecked == true,
            0x2000 => ReserveCheck.IsChecked == true,
            0x10000 => FreeCheck.IsChecked == true,
            _ => true
        };
        bool typeMatches = region.Type switch
        {
            0x1000000 => ImageCheck.IsChecked == true,
            0x40000 => MappedCheck.IsChecked == true,
            0x20000 => PrivateCheck.IsChecked == true,
            _ => true
        };
        string search = SearchTextBox.Text.Trim();
        bool searchMatches =
            search.Length == 0 ||
            region.BaseAddressText.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            region.ModuleName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            region.SectionName.Contains(search, StringComparison.OrdinalIgnoreCase);
        return stateMatches && typeMatches && searchMatches;
    }

    private async void OnRegionGridDoubleClick(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (DataContext is MainViewModel viewModel &&
            RegionGrid.SelectedItem is MemoryRegionInfo region)
        {
            await viewModel.NavigateToAddressAsync(region.BaseAddress);
        }
    }

    private void ReportColumnWidths()
    {
        string? path = Environment.GetEnvironmentVariable(
            "DOGEDEBUGGER_LAYOUT_LOG");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string columns = string.Join(
            ", ",
            RegionGrid.Columns.Select(column =>
                $"{column.Header}={column.ActualWidth:0.###}"));
        File.AppendAllText(
            path,
            $"{DateTime.Now:O} MemoryMap grid={RegionGrid.ActualWidth:0.###} " +
            $"viewport={RegionGrid.Columns.Sum(static column => column.ActualWidth):0.###} " +
            $"columns: {columns}{Environment.NewLine}");
    }
}
