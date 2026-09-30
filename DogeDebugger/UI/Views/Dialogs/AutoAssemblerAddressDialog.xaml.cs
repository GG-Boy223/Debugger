using System.Windows;

namespace DogeDebugger.UI.Views.Dialogs;

public partial class AutoAssemblerAddressDialog : Window
{
    public AutoAssemblerAddressDialog(string title, string prompt, string initialValue)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueTextBox.Text = initialValue;
        Loaded += OnLoaded;
    }

    public string Value => ValueTextBox.Text;

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        ValueTextBox.Focus();
        ValueTextBox.SelectAll();
    }

    private void OnOkClick(object sender, RoutedEventArgs eventArgs)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs eventArgs)
    {
        DialogResult = false;
        Close();
    }
}
