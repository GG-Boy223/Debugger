using System.Windows.Controls;
using System.Windows.Input;
using DogeDebugger.Core.Disassembly;

namespace DogeDebugger.UI.Views.Panels;

public partial class DisassemblyView : UserControl
{
    public DisassemblyView()
    {
        InitializeComponent();
    }

    private async void OnInstructionGridDoubleClick(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (DataContext is MainViewModel viewModel &&
            InstructionGrid.SelectedItem is InstructionSnapshot instruction)
        {
            await viewModel.NavigateToAddressAsync(instruction.Address);
        }
    }
}
