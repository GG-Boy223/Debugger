using System.Windows;
using System.Windows.Controls;
using DogeDebugger.Core.Rtti;
using DogeDebugger.UI.ViewModels.Panels;

namespace DogeDebugger.UI.Views.Panels;

public partial class RttiView : UserControl
{
    public RttiView()
    {
        InitializeComponent();
    }

    private void OnClassTreeSelectedItemChanged(
        object sender,
        RoutedPropertyChangedEventArgs<object> eventArgs)
    {
        if (DataContext is RttiViewModel viewModel &&
            eventArgs.NewValue is RttiClassDefinition definition)
        {
            viewModel.SelectedClass = definition;
        }
    }
}
