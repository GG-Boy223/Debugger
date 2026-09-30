using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DogeDebugger.Core.Cache;
using DogeDebugger.UI.ViewModels.Dialogs.CacheManager;

namespace DogeDebugger.UI.Views.Dialogs.CacheManager;

public partial class StaticAnalysisCacheTabView : UserControl
{
    public StaticAnalysisCacheTabView()
    {
        InitializeComponent();
    }

    private StaticAnalysisCacheTabViewModel? ViewModel =>
        DataContext as StaticAnalysisCacheTabViewModel;

    private void OnHeaderSelectAllChanged(object sender, RoutedEventArgs eventArgs)
    {
        StaticAnalysisCacheTabViewModel? viewModel = ViewModel;
        if (viewModel is null ||
            sender is not CheckBox selectAll)
        {
            return;
        }

        bool isSelected = selectAll.IsChecked == true;
        foreach (StaticCacheItem item in viewModel.Items)
        {
            item.IsSelected = isSelected;
        }
    }

    private void OnCacheListViewMouseDoubleClick(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        StaticAnalysisCacheTabViewModel? viewModel = ViewModel;
        if (viewModel is null ||
            eventArgs.OriginalSource is not FrameworkElement source ||
            source.DataContext is not StaticCacheItem item)
        {
            return;
        }

        viewModel.OpenInExplorerCommand.Execute(item);
        eventArgs.Handled = true;
    }
}
