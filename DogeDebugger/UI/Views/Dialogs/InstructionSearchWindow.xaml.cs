using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using DogeDebugger.Core.Disassembly;
using DogeDebugger.Core.Modules;

namespace DogeDebugger.UI.Views.Dialogs;

public partial class InstructionSearchWindow : Window
{
    private readonly ObservableCollection<InstructionSearchModuleOption> _modules;
    private readonly ObservableCollection<InstructionSearchModuleOption> _visibleModules = [];
    private readonly ulong _assemblyAddress;
    private readonly int _bitness;
    private readonly AssemblySyntax _syntax;

    public InstructionSearchWindow(
        IReadOnlyList<ModuleDescriptor> modules,
        string instructionText,
        ulong assemblyAddress,
        int bitness,
        AssemblySyntax syntax)
    {
        InitializeComponent();
        _assemblyAddress = assemblyAddress;
        _bitness = bitness;
        _syntax = syntax;
        _modules = new ObservableCollection<InstructionSearchModuleOption>(
            modules.Select(module => new InstructionSearchModuleOption(module)));
        if (_modules.Count > 0)
        {
            _modules[0].IsSelected = true;
        }

        InstructionBox.Text = instructionText;
        ModuleListView.ItemsSource = _visibleModules;
        RebuildModuleList();
        Loaded += OnLoaded;
    }

    public CommandSearchRequest? Request { get; private set; }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        InstructionBox.Focus();
        InstructionBox.SelectAll();
    }

    private void OnModuleFilterChanged(
        object sender,
        TextChangedEventArgs eventArgs)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        RebuildModuleList();
    }

    private void RebuildModuleList()
    {
        string filter = SearchBox.Text.Trim();
        _visibleModules.Clear();
        foreach (InstructionSearchModuleOption module in _modules)
        {
            if (filter.Length == 0 ||
                module.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                module.FilePath.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                module.BaseAddressText.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                _visibleModules.Add(module);
            }
        }

        UpdateSelectionCount();
    }

    private void OnModuleSelectionChanged(
        object sender,
        RoutedEventArgs eventArgs) =>
        UpdateSelectionCount();

    private void UpdateSelectionCount()
    {
        SelectionCountText.Text =
            $"已选择 {_modules.Count(static module => module.IsSelected)} / {_modules.Count} 个模块";
    }

    private void OnSelectAllClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        foreach (InstructionSearchModuleOption module in _visibleModules)
        {
            module.IsSelected = true;
        }

        ModuleListView.Items.Refresh();
        UpdateSelectionCount();
    }

    private void OnSelectNoneClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        foreach (InstructionSearchModuleOption module in _visibleModules)
        {
            module.IsSelected = false;
        }

        ModuleListView.Items.Refresh();
        UpdateSelectionCount();
    }

    private void OnOkClick(object sender, RoutedEventArgs eventArgs)
    {
        string instruction = InstructionBox.Text.Trim();
        ModuleDescriptor[] selected = _modules
            .Where(static module => module.IsSelected)
            .Select(static module => module.Module)
            .ToArray();
        if (instruction.Length == 0 || selected.Length == 0)
        {
            return;
        }

        Request = new CommandSearchRequest
        {
            AssemblyText = instruction,
            AssemblyAddress = _assemblyAddress,
            Bitness = _bitness,
            SyntaxFormat = _syntax,
            ScanModules = selected
        };
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs eventArgs)
    {
        DialogResult = false;
    }
}

public sealed class InstructionSearchModuleOption : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private bool _isSelected;

    public InstructionSearchModuleOption(ModuleDescriptor module)
    {
        Module = module;
    }

    public ModuleDescriptor Module { get; }

    public string Name => Module.Name;

    public string BaseAddressText => $"0x{Module.BaseAddress:X}";

    public string SizeText => $"({Module.Size / 1024} KB)";

    public string FilePath => Module.FilePath;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
