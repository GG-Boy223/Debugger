using System.Runtime.InteropServices;
using System.Windows;
using Wpf.Ui.Controls;

namespace DogeDebugger.UI.Views.Dialogs;

public partial class AboutWindow : FluentWindow
{
    private const string ProductVersion = "5.0.0";

    public AboutWindow()
    {
        InitializeComponent();

        VersionText.Text = $"v{ProductVersion}（本地免登录）";
        VersionDetailText.Text = $"DogeDebugger v{ProductVersion}（本地免登录）";
        FrameworkText.Text = RuntimeInformation.FrameworkDescription;
        OperatingSystemText.Text = $"Windows {Environment.OSVersion.Version}";
        ArchitectureText.Text = RuntimeInformation.ProcessArchitecture.ToString();
        CopyrightText.Text =
            $"© {DateTime.Now.Year} DogeDebugger. All rights reserved.";
    }

    private void OnCloseClick(object sender, RoutedEventArgs eventArgs)
    {
        Close();
    }
}
