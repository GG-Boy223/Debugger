using System.Windows;
using System.Windows.Controls;
using DogeDebugger.Core.Search;
using DogeDebugger.UI.ViewModels.Panels;

namespace DogeDebugger.UI.Views.Dialogs;

public partial class ManualAddressWindow : Window
{
    private readonly Func<ulong, MemoryValueKind, string?> _valueReader;

    public ManualAddressWindow(
        Func<ulong, MemoryValueKind, string?> valueReader)
    {
        _valueReader = valueReader;
        InitializeComponent();
        TypeCombo.ItemsSource = new[]
        {
            new ValueTypeOption("单字节", MemoryValueKind.Byte),
            new ValueTypeOption("双字节", MemoryValueKind.Int16),
            new ValueTypeOption("四字节", MemoryValueKind.Int32),
            new ValueTypeOption("八字节", MemoryValueKind.Int64),
            new ValueTypeOption("浮点数", MemoryValueKind.Single),
            new ValueTypeOption("双精度浮点", MemoryValueKind.Double),
            new ValueTypeOption("字符串", MemoryValueKind.Utf8String),
            new ValueTypeOption("字节数组 (特征码/AOB)", MemoryValueKind.ByteArray)
        };
        TypeCombo.SelectedIndex = 2;
    }

    public SavedAddressRowSource? Result { get; private set; }

    private void OnAddressTextChanged(
        object sender,
        TextChangedEventArgs eventArgs) =>
        UpdateValuePreview();

    private void OnTypeSelectionChanged(
        object sender,
        SelectionChangedEventArgs eventArgs) =>
        UpdateValuePreview();

    private void UpdateValuePreview()
    {
        if (ValuePreview is null)
        {
            return;
        }

        if (!SavedAddressTableCodec.TryParseAddress(
                AddressBox.Text,
                out ulong address) ||
            TypeCombo.SelectedItem is not ValueTypeOption valueType)
        {
            ValuePreview.Text = "=???";
            return;
        }

        string? value = _valueReader(address, valueType.Value);
        ValuePreview.Text = string.IsNullOrEmpty(value)
            ? "=???"
            : "=" + value;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs eventArgs)
    {
        if (!SavedAddressTableCodec.TryParseAddress(
                AddressBox.Text,
                out ulong address) ||
            TypeCombo.SelectedItem is not ValueTypeOption valueType)
        {
            ValuePreview.Text = "=???";
            return;
        }

        Result = new SavedAddressRowSource
        {
            Address = address,
            Description = DescriptionBox.Text,
            ValueKind = valueType.Value,
            Value = ValuePreview.Text.TrimStart('='),
            IsHexadecimal = HexCheck.IsChecked == true,
            IsSigned = SignedCheck.IsChecked == true
        };
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs eventArgs)
    {
        DialogResult = false;
    }
}
