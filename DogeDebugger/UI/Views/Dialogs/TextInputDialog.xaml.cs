using System.Windows;

namespace DogeDebugger.UI.Views.Dialogs;

public partial class TextInputDialog : Window
{
    public TextInputDialog(
        string title,
        string prompt,
        string initialValue,
        double inputHeight = 30,
        double width = 450,
        string confirmText = "确定")
    {
        InitializeComponent();
        Title = title;
        Width = width;
        PromptText.Text = prompt;
        ValueTextBox.Text = initialValue;
        ValueTextBox.Height = inputHeight;
        ConfirmButton.Content = confirmText;
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
    }

    private void OnCancelClick(object sender, RoutedEventArgs eventArgs)
    {
        DialogResult = false;
    }
}
