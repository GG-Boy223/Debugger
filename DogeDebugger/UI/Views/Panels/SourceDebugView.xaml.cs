using System.Windows;
using System.Windows.Controls;
using DogeDebugger.UI.ViewModels.Panels;

namespace DogeDebugger.UI.Views.Panels;

public partial class SourceDebugView : UserControl
{
    public SourceDebugView()
    {
        InitializeComponent();
    }

    private SourceDebugPanelViewModel? ViewModel =>
        DataContext as SourceDebugPanelViewModel;

    private void OnCloseDocumentClick(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is FrameworkElement { Tag: SourceDocument document } &&
            ViewModel is { } viewModel &&
            viewModel.CloseDocumentCommand.CanExecute(document))
        {
            viewModel.CloseDocumentCommand.Execute(document);
            eventArgs.Handled = true;
        }
    }

    private void OnCloseSelectedDocumentClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { SelectedDocument: { } document } viewModel)
        {
            return;
        }

        viewModel.CloseDocumentCommand.Execute(document);
        eventArgs.Handled = true;
    }

    private void OnCloseOtherDocumentsClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { SelectedDocument: { } document } viewModel)
        {
            return;
        }

        viewModel.CloseOthersCommand.Execute(document);
        eventArgs.Handled = true;
    }
}
