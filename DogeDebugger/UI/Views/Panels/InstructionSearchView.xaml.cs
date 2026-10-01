using System.Windows.Controls;
using System.Windows.Input;
using DogeDebugger.Core.Disassembly;

namespace DogeDebugger.UI.Views.Panels;

public partial class InstructionSearchView : UserControl
{
    public InstructionSearchView()
    {
        InitializeComponent();
    }

    private void OnResultGridDoubleClick(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (DataContext is MainViewModel viewModel &&
            ResultGrid.SelectedItem is CommandSearchResultItem result)
        {
            viewModel.InstructionSearchPanel.NavigateToResult(result);
            if (System.Windows.Window.GetWindow(this) is MainWindow mainWindow)
            {
                mainWindow.ShowDisassemblyPanel();
            }
        }
    }
}
