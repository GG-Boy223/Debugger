using System.Windows.Controls;
using System.Windows.Input;
using DogeDebugger.UI.ViewModels.Panels;

namespace DogeDebugger.UI.Views.Panels;

public partial class MemorySearchView : UserControl
{
    public MemorySearchView()
    {
        InitializeComponent();
    }

    private void OnResultsMouseDoubleClick(object sender, MouseButtonEventArgs eventArgs)
    {
        if (DataContext is MemorySearchViewModel viewModel &&
            viewModel.OpenSelectedResultCommand.CanExecute(null))
        {
            viewModel.OpenSelectedResultCommand.Execute(null);
        }
    }
}
