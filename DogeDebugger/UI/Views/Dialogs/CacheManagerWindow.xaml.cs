using System.Windows;
using DogeDebugger.UI.ViewModels.Dialogs;
using Wpf.Ui.Controls;

namespace DogeDebugger.UI.Views.Dialogs;

public partial class CacheManagerWindow : FluentWindow
{
    public CacheManagerWindow()
    {
        InitializeComponent();
        DataContext = new CacheManagerViewModel();
    }
}
