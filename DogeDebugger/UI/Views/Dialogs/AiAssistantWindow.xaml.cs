using System.Windows;
using DogeDebugger.Core.Settings;
using DogeDebugger.UI.ViewModels.Dialogs;
using Wpf.Ui.Controls;

namespace DogeDebugger.UI.Views.Dialogs;

public partial class AiAssistantWindow : FluentWindow
{
    public AiAssistantWindow(AppSettings settings)
    {
        InitializeComponent();
        DataContext = new AiAssistantViewModel(settings);
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is AiAssistantViewModel viewModel)
        {
            await viewModel.LoadModelsCommand.ExecuteAsync(null);
        }
    }
}
