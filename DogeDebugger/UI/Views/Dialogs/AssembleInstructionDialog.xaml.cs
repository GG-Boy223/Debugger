using System.Windows;

namespace DogeDebugger.UI.Views.Dialogs;

public partial class AssembleInstructionDialog : Window
{
    public AssembleInstructionDialog(
        ulong address,
        string originalInstruction)
    {
        InitializeComponent();
        AddressText.Text = $"地址: 0x{address:X}";
        OriginalInstructionText.Text = $"原始指令: {originalInstruction}";
        InstructionBox.Text = originalInstruction;
        Loaded += OnLoaded;
    }

    public string InstructionText => InstructionBox.Text.Trim();

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        InstructionBox.Focus();
        InstructionBox.SelectAll();
    }

    private void OnWriteClick(object sender, RoutedEventArgs eventArgs)
    {
        if (InstructionText.Length == 0)
        {
            return;
        }

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs eventArgs)
    {
        DialogResult = false;
    }
}
