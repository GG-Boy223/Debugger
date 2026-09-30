using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using DogeDebugger.Core.Modules;
using DogeDebugger.UI.ViewModels.Dialogs;
using Wpf.Ui.Controls;

namespace DogeDebugger.UI.Views.Dialogs;

public partial class ModuleSelectionDialog : Window
{
    private readonly List<ModuleSelectionItem> _modules;
    private readonly ObservableCollection<ModuleSelectionItem> _visibleModules = [];
    private string _filter = string.Empty;

    public ModuleSelectionDialog(
        IReadOnlyList<ModuleDescriptor> modules,
        ModuleDescriptor? preselectedModule = null)
    {
        InitializeComponent();
        ArgumentNullException.ThrowIfNull(modules);
        _modules = modules
            .Select(module => new ModuleSelectionItem(
                module,
                preselectedModule is not null
                    ? ReferenceEquals(module, preselectedModule) ||
                      module.BaseAddress == preselectedModule.BaseAddress &&
                      string.Equals(
                          module.Name,
                          preselectedModule.Name,
                          StringComparison.OrdinalIgnoreCase)
                    : module.IsMainModule))
            .ToList();
        foreach (ModuleSelectionItem item in _modules)
        {
            item.PropertyChanged += (_, _) => UpdateSelectionText();
        }

        ModuleList.ItemsSource = _visibleModules;
        ApplyFilter();
    }

    public IReadOnlyList<ModuleDescriptor> SelectedModules { get; private set; } = [];

    private void OnSearchTextChanged(object sender, TextChangedEventArgs eventArgs)
    {
        _filter = SearchBox.Text.Trim();
        ApplyFilter();
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs eventArgs)
    {
        foreach (ModuleSelectionItem item in _visibleModules)
        {
            item.IsSelected = true;
        }
    }

    private void OnClearSelectionClick(object sender, RoutedEventArgs eventArgs)
    {
        foreach (ModuleSelectionItem item in _visibleModules)
        {
            item.IsSelected = false;
        }
    }

    private void OnConfirmClick(object sender, RoutedEventArgs eventArgs)
    {
        ModuleSelectionItem[] selected = _modules
            .Where(static item => item.IsSelected)
            .ToArray();
        if (selected.Length == 0)
        {
            System.Windows.MessageBox.Show(
                this,
                "请至少选择一个模块。",
                "选择要分析的模块",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        SelectedModules = selected.Select(static item => item.Module).ToArray();
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs eventArgs)
    {
        DialogResult = false;
    }

    private void ApplyFilter()
    {
        _visibleModules.Clear();
        foreach (ModuleSelectionItem item in _modules)
        {
            if (_filter.Length == 0 ||
                item.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase))
            {
                _visibleModules.Add(item);
            }
        }

        UpdateSelectionText();
    }

    private void UpdateSelectionText()
    {
        int selected = _modules.Count(static item => item.IsSelected);
        SelectionText.Text = $"已选择 {selected} / {_modules.Count} 个模块";
    }
}
