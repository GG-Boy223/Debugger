using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using DogeDebugger.Core.Process;

namespace DogeDebugger.UI.Views.Dialogs;

public partial class ProcessSelectionWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ProcessSelectionMode _mode;
    private readonly CollectionViewSource _applicationView = new();
    private readonly CollectionViewSource _allProcessView = new();
    private readonly WindowPickerSession _windowPicker;
    private bool _isRefreshing;
    private bool _summaryUpdatePending;

    public ProcessSelectionWindow(
        MainViewModel viewModel,
        ProcessSelectionMode mode = ProcessSelectionMode.OpenProcess)
    {
        _viewModel = viewModel;
        _mode = mode;
        InitializeComponent();
        ApplyMode();
        InitializeViews();
        _windowPicker = new WindowPickerSession(this, WindowPickerButton);
        _windowPicker.Picked += OnWindowPicked;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    public ProcessDescriptor? SelectedProcess => _viewModel.SelectedProcess;

    private void ApplyMode()
    {
        (Title, ConfirmButton.Content) = _mode switch
        {
            ProcessSelectionMode.OpenProcess => ("打开进程", "打开进程"),
            ProcessSelectionMode.Attach => ("附加到进程", "附加"),
            ProcessSelectionMode.Inject => ("选择注入目标", "选择"),
            _ => ("选择进程", "确定")
        };
    }

    private void InitializeViews()
    {
        _applicationView.Source = _viewModel.Processes;
        _allProcessView.Source = _viewModel.Processes;
        _applicationView.Filter += OnApplicationFilter;
        _allProcessView.Filter += OnProcessFilter;
        _applicationView.SortDescriptions.Add(new SortDescription(
            nameof(ProcessDescriptor.Name),
            ListSortDirection.Ascending));
        _allProcessView.SortDescriptions.Add(new SortDescription(
            nameof(ProcessDescriptor.ProcessId),
            ListSortDirection.Ascending));

        AppListBox.ItemsSource = _applicationView.View;
        ProcessGrid.ItemsSource = _allProcessView.View;
        _viewModel.Processes.CollectionChanged += OnProcessesChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        SearchBox.Focus();
        UpdateSummary();
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        _viewModel.Processes.CollectionChanged -= OnProcessesChanged;
        _windowPicker.Picked -= OnWindowPicked;
        _windowPicker.Dispose();
    }

    private void OnProcessesChanged(
        object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs eventArgs)
    {
        if (_summaryUpdatePending)
        {
            return;
        }

        _summaryUpdatePending = true;
        Dispatcher.BeginInvoke(
            () =>
            {
                _summaryUpdatePending = false;
                RefreshViews();
            },
            DispatcherPriority.Background);
    }

    private void OnApplicationFilter(object sender, FilterEventArgs eventArgs)
    {
        eventArgs.Accepted = eventArgs.Item is ProcessDescriptor process &&
                             process.HasWindow &&
                             MatchesSearch(process);
    }

    private void OnProcessFilter(object sender, FilterEventArgs eventArgs)
    {
        eventArgs.Accepted = eventArgs.Item is ProcessDescriptor process &&
                             MatchesSearch(process);
    }

    private bool MatchesSearch(ProcessDescriptor process)
    {
        string search = SearchBox.Text.Trim();
        if (search.Length == 0)
        {
            return true;
        }

        return Contains(process.Name, search) ||
               Contains(process.WindowTitle, search) ||
               Contains(process.FilePath, search) ||
               Contains(process.PidDisplay, search);
    }

    private static bool Contains(string? value, string search)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private async void OnRefreshClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        await RefreshProcessesAsync();
    }

    private async void OnWindowPreviewKeyDown(
        object sender,
        KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.F5)
        {
            eventArgs.Handled = true;
            await RefreshProcessesAsync();
        }
    }

    private async Task RefreshProcessesAsync()
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        try
        {
            await _viewModel.RefreshProcessesCommand.ExecuteAsync(null);
            RefreshViews();
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void RefreshViews()
    {
        _applicationView.View.Refresh();
        _allProcessView.View.Refresh();
        UpdateSummary();
    }

    private void OnSearchTextChanged(
        object sender,
        TextChangedEventArgs eventArgs)
    {
        RefreshViews();
    }

    private void OnProcessSelectionChanged(
        object sender,
        SelectionChangedEventArgs eventArgs)
    {
        if (sender is not ListBox listBox ||
            listBox.SelectedItem is not ProcessDescriptor process)
        {
            return;
        }

        _viewModel.SelectedProcess = process;
        if (!ReferenceEquals(sender, AppListBox))
        {
            AppListBox.SelectedItem = null;
        }

        if (!ReferenceEquals(sender, ProcessGrid))
        {
            ProcessGrid.SelectedItem = null;
        }
    }

    private void OnProcessItemDoubleClick(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton == MouseButton.Left &&
            eventArgs.ClickCount > 1)
        {
            CompleteSelection();
            eventArgs.Handled = true;
        }
    }

    private void OnWindowPickerMouseDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _windowPicker.Start();
        eventArgs.Handled = true;
    }

    private async void OnWindowPicked(WindowPickResult result)
    {
        ProcessDescriptor? process = _viewModel.Processes.FirstOrDefault(
            candidate => candidate.ProcessId == result.ProcessId);
        if (process is null)
        {
            await RefreshProcessesAsync();
            process = _viewModel.Processes.FirstOrDefault(
                candidate => candidate.ProcessId == result.ProcessId);
        }

        if (process is null)
        {
            MessageBox.Show(
                this,
                $"未找到 PID {result.ProcessId} 对应的进程，进程可能已经退出或无权限访问。",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!_allProcessView.View.Cast<ProcessDescriptor>().Any(
                candidate => candidate.ProcessId == process.ProcessId))
        {
            SearchBox.Text = string.Empty;
        }

        bool useApplicationList =
            (!string.IsNullOrWhiteSpace(result.Title) || process.HasWindow) &&
            _applicationView.View.Cast<ProcessDescriptor>().Any(
                candidate => candidate.ProcessId == process.ProcessId);
        SelectProcess(process, useApplicationList);
    }

    private void SelectProcess(
        ProcessDescriptor process,
        bool useApplicationList)
    {
        _viewModel.SelectedProcess = process;
        if (useApplicationList)
        {
            ProcessTabs.SelectedItem = ApplicationTab;
            AppListBox.SelectedItem = process;
            AppListBox.ScrollIntoView(process);
            AppListBox.Focus();
            return;
        }

        ProcessTabs.SelectedItem = AllProcessesTab;
        ProcessGrid.SelectedItem = process;
        ProcessGrid.ScrollIntoView(process);
        ProcessGrid.Focus();
    }

    private void OnConfirmClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        CompleteSelection();
        eventArgs.Handled = true;
    }

    private void CompleteSelection()
    {
        ProcessDescriptor? process = GetSelectedProcess();
        if (process is null)
        {
            MessageBox.Show(
                this,
                "请先选择一个进程。",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _viewModel.SelectedProcess = process;
        DialogResult = true;
    }

    private ProcessDescriptor? GetSelectedProcess()
    {
        return ProcessTabs.SelectedItem == AllProcessesTab
            ? ProcessGrid.SelectedItem as ProcessDescriptor
            : AppListBox.SelectedItem as ProcessDescriptor;
    }

    private void OnCancelClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        DialogResult = false;
        eventArgs.Handled = true;
    }

    private void UpdateSummary()
    {
        int processCount = _allProcessView.View.Cast<ProcessDescriptor>().Count();
        int applicationCount = _applicationView.View.Cast<ProcessDescriptor>().Count();
        ProcessCountText.Text = $"共 {processCount} 个进程，{applicationCount} 个应用";
    }
}
