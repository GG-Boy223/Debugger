using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DogeDebugger.Core.Cache;
using DogeDebugger.UI.Views.Dialogs.CacheManager;

namespace DogeDebugger.UI.ViewModels.Dialogs.CacheManager;

public partial class StaticAnalysisCacheTabViewModel :
    ObservableObject,
    ICacheManagerTabViewModel
{
    private readonly Lazy<UserControl> _view;

    public StaticAnalysisCacheTabViewModel()
    {
        _view = new Lazy<UserControl>(() => new StaticAnalysisCacheTabView
        {
            DataContext = this
        });
        Refresh();
    }

    public string Header => "静态缓存";

    public int Count => Items.Count;

    public UserControl View => _view.Value;

    public ObservableCollection<StaticCacheItem> Items { get; } = [];

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private int _selectedCount;

    [RelayCommand]
    private void Refresh()
    {
        foreach (StaticCacheItem item in Items)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }

        Items.Clear();
        foreach (string path in StaticAnalysisCacheStore.EnumerateAllFiles())
        {
            StaticCacheItem item = StaticCacheItem.FromFile(path);
            item.PropertyChanged += OnItemPropertyChanged;
            Items.Add(item);
        }

        OnPropertyChanged(nameof(Count));
        UpdateSummary();
        UpdateSelectionState();
    }

    [RelayCommand(CanExecute = nameof(CanModifySelection))]
    private void SelectAll()
    {
        foreach (StaticCacheItem item in Items)
        {
            item.IsSelected = true;
        }
    }

    [RelayCommand(CanExecute = nameof(CanModifySelection))]
    private void InvertSelection()
    {
        foreach (StaticCacheItem item in Items)
        {
            item.IsSelected = !item.IsSelected;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private async Task DeleteSelectedAsync()
    {
        StaticCacheItem[] selected = Items
            .Where(static item => item.IsSelected)
            .ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        Wpf.Ui.Controls.MessageBox prompt = new()
        {
            Title = "删除缓存",
            Content = $"确认删除选中的 {selected.Length} 个缓存文件？该操作不可恢复。",
            PrimaryButtonText = "删除",
            SecondaryButtonText = "取消",
            CloseButtonText = string.Empty,
            IsCloseButtonEnabled = false,
            Owner = Application.Current?.Windows
                .OfType<Window>()
                .FirstOrDefault(static window => window.IsActive),
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        Wpf.Ui.Controls.MessageBoxResult result = await prompt.ShowDialogAsync();
        if (result != Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            return;
        }

        int failed = 0;
        foreach (StaticCacheItem item in selected)
        {
            if (!StaticAnalysisCacheStore.DeleteManagedFile(item.FullPath))
            {
                failed++;
            }
        }

        Refresh();
        if (failed > 0)
        {
            System.Windows.MessageBox.Show(
                $"有 {failed} 个文件删除失败（可能正被占用）。",
                "删除缓存",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void OpenInExplorer(StaticCacheItem? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            if (!File.Exists(item.FullPath))
            {
                OpenDirectory(StaticAnalysisCacheStore.AnalysisRoot);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{item.FullPath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                "打开资源管理器失败：" + exception.Message,
                "静态缓存",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void OpenCacheFolder()
    {
        try
        {
            string root = StaticAnalysisCacheStore.AnalysisRoot;
            Directory.CreateDirectory(root);
            OpenDirectory(root);
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                "打开缓存目录失败：" + exception.Message,
                "静态缓存",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(StaticCacheItem.IsSelected))
        {
            UpdateSelectionState();
        }
    }

    private bool CanModifySelection()
    {
        return Items.Count > 0;
    }

    private bool CanDeleteSelected()
    {
        return SelectedCount > 0;
    }

    private void UpdateSummary()
    {
        long totalBytes = Items.Sum(static item => item.SizeBytes);
        int dumpCount = Items.Count(static item => item.IsDump);
        long dumpBytes = Items
            .Where(static item => item.IsDump)
            .Sum(static item => item.SizeBytes);
        int analyzeCount = Items.Count - dumpCount;
        long analyzeBytes = totalBytes - dumpBytes;
        SummaryText =
            $"共 {Items.Count} 个文件，占用 {StaticAnalysisCacheStore.FormatSize(totalBytes)}" +
            $"（分析 {analyzeCount} 个 {StaticAnalysisCacheStore.FormatSize(analyzeBytes)}" +
            $"，转储 {dumpCount} 个 {StaticAnalysisCacheStore.FormatSize(dumpBytes)}）";
    }

    private void UpdateSelectionState()
    {
        SelectedCount = Items.Count(static item => item.IsSelected);
        SelectAllCommand.NotifyCanExecuteChanged();
        InvertSelectionCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    private static void OpenDirectory(string directory)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{directory}\"",
            UseShellExecute = true
        });
    }
}
