using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace DogeDebugger.UI.Controls;

public partial class TablePanel : UserControl
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(
            nameof(Header),
            typeof(string),
            typeof(TablePanel),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(IEnumerable),
            typeof(TablePanel),
            new PropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty ItemCountProperty =
        DependencyProperty.Register(
            nameof(ItemCount),
            typeof(int),
            typeof(TablePanel),
            new PropertyMetadata(0));

    public TablePanel()
    {
        InitializeComponent();
    }

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public int ItemCount
    {
        get => (int)GetValue(ItemCountProperty);
        private set => SetValue(ItemCountProperty, value);
    }

    private static void OnItemsSourceChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        TablePanel panel = (TablePanel)dependencyObject;
        panel.ItemCount = eventArgs.NewValue is ICollection collection
            ? collection.Count
            : 0;
    }
}
