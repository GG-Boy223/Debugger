using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace DogeDebugger.UI.Views.Panels;

public partial class LogView : UserControl
{
    private MainViewModel? _viewModel;
    private ICollectionView? _view;

    public LogView()
    {
        InitializeComponent();
        DataContextChanged += OnLogDataContextChanged;
    }

    private void OnLogDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        DetachViewModel();
        if (IsLoaded && eventArgs.NewValue is MainViewModel viewModel)
        {
            AttachViewModel(viewModel);
        }
    }

    private void OnLogViewLoaded(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (DataContext is MainViewModel viewModel)
        {
            AttachViewModel(viewModel);
        }
    }

    private void OnLogViewUnloaded(
        object sender,
        RoutedEventArgs eventArgs)
    {
        DetachViewModel();
    }

    private void AttachViewModel(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        _viewModel.LogMessages.CollectionChanged += OnLogCollectionChanged;
        _view = CollectionViewSource.GetDefaultView(_viewModel.LogMessages);
        _view.Filter = FilterLogRow;
        _view.Refresh();
        LogListBox.Tag = LogShowTimestamp.IsChecked == true;
    }

    private void DetachViewModel()
    {
        if (_viewModel is not null)
        {
            _viewModel.LogMessages.CollectionChanged -= OnLogCollectionChanged;
        }

        _view = null;
        _viewModel = null;
    }

    private void OnClearLogClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        _viewModel?.LogMessages.Clear();
    }

    private void OnLogFilterChanged(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (LogListBox is null)
        {
            return;
        }

        LogListBox.Tag = LogShowTimestamp.IsChecked == true;
        _view?.Refresh();
        ScrollToEndIfNeeded();
    }

    private bool FilterLogRow(object item)
    {
        if (item is not LogRow row)
        {
            return false;
        }

        return row.Category switch
        {
            LogCategory.System => LogFilterSystem.IsChecked == true,
            LogCategory.DebugEvent => LogFilterDebugEvents.IsChecked == true,
            _ => true
        };
    }

    private void OnLogCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs eventArgs)
    {
        ScrollToEndIfNeeded();
    }

    private void ScrollToEndIfNeeded()
    {
        if (LogAutoScroll.IsChecked != true ||
            _view is null ||
            !_view.Cast<object>().Any())
        {
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            () => LogListBox.ScrollIntoView(_view.Cast<object>().Last()));
    }
}
