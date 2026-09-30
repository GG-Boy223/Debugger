using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DogeDebugger.Core.Modules;
using DogeDebugger.UI.ViewModels.Panels;
using DogeDebugger.UI.Views.Dialogs;

namespace DogeDebugger.UI.Views.Panels;

public partial class CrossRefListView : UserControl
{
    public CrossRefListView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => ConfigureViewModel();
    }

    private CrossRefPanelViewModel? ViewModel =>
        DataContext as CrossRefPanelViewModel;

    private void OnDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.NewValue is not CrossRefPanelViewModel viewModel)
        {
            return;
        }

        ConfigureViewModel(viewModel);
    }

    private void ConfigureViewModel()
    {
        if (DataContext is CrossRefPanelViewModel viewModel)
        {
            ConfigureViewModel(viewModel);
        }
    }

    private void ConfigureViewModel(CrossRefPanelViewModel viewModel)
    {
        viewModel.ShowModuleSelectionDialog = SelectModule;
        viewModel.ShowError = ShowError;
    }

    private ModuleDescriptor? SelectModule(
        IReadOnlyList<ModuleDescriptor> modules,
        ModuleDescriptor? current)
    {
        ModuleSelectionDialog dialog = new(modules, current)
        {
            Owner = Window.GetWindow(this)
        };
        return dialog.ShowDialog() == true
            ? dialog.SelectedModules.FirstOrDefault()
            : null;
    }

    private void ShowError(string title, string message)
    {
        System.Windows.MessageBox.Show(
            Window.GetWindow(this),
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void OnTargetKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Enter ||
            ViewModel is not { } viewModel ||
            !viewModel.ResolveTargetCommand.CanExecute(null))
        {
            return;
        }

        viewModel.ResolveTargetCommand.Execute(null);
        eventArgs.Handled = true;
    }

    private void OnUseRipClick(object sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        viewModel.TargetInput = "RIP";
        TargetBox.Focus();
        TargetBox.CaretIndex = TargetBox.Text.Length;
        if (viewModel.ResolveTargetCommand.CanExecute(null))
        {
            viewModel.ResolveTargetCommand.Execute(null);
        }
    }

    private void OnFunctionGridDoubleClick(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (ViewModel is { } viewModel &&
            FunctionGrid.SelectedItem is FunctionDisplayItem function)
        {
            viewModel.NavigateToDisassembly(function.StartVa);
        }
    }

    private void OnXrefGridDoubleClick(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (ViewModel is { } viewModel &&
            XrefGrid.SelectedItem is XrefDisplayItem xref)
        {
            viewModel.NavigateToXrefSource(xref);
        }
    }

    private void OnFunctionMenuClick(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem { Tag: string action } ||
            ViewModel is not { } viewModel ||
            FunctionGrid.SelectedItem is not FunctionDisplayItem function)
        {
            return;
        }

        switch (action)
        {
            case "navigate":
                viewModel.NavigateToDisassembly(function.StartVa);
                break;
            case "copyAddress":
                viewModel.CopyFunctionAddress(function);
                break;
            case "copyName":
                viewModel.CopyFunctionName(function);
                break;
        }
    }

    private void OnXrefMenuClick(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem { Tag: string action } ||
            ViewModel is not { } viewModel ||
            XrefGrid.SelectedItem is not XrefDisplayItem xref)
        {
            return;
        }

        switch (action)
        {
            case "navigateSource":
                viewModel.NavigateToXrefSource(xref);
                break;
            case "navigateCaller":
                viewModel.NavigateToCaller(xref);
                break;
            case "copyAddress":
                viewModel.CopyXrefAddress(xref);
                break;
            case "copyCaller":
                viewModel.CopyCallerName(xref);
                break;
        }
    }
}
