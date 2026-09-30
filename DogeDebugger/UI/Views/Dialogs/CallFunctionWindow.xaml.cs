using System.Globalization;
using System.Windows;
using DogeDebugger.Core.Process;

namespace DogeDebugger.UI.Views.Dialogs;

public partial class CallFunctionWindow : Window
{
    private readonly MainViewModel _viewModel;

    public CallFunctionWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        AddressBox.Text = viewModel.SelectedInstruction is { Address: not 0 } instruction
            ? $"0x{instruction.Address:X}"
            : viewModel.AddressInput;
        Loaded += (_, _) => AddressBox.Focus();
    }

    private void OnExpandToggleClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        bool expanded = AdditionalParametersGrid.Visibility != Visibility.Visible;
        AdditionalParametersGrid.Visibility = expanded
            ? Visibility.Visible
            : Visibility.Collapsed;
        ExpandToggle.Content = expanded
            ? "收起更多参数 (5-8)"
            : "展开更多参数 (5-8)";
    }

    private async void OnExecuteClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (!TryParseHex(AddressBox.Text, out ulong functionAddress))
        {
            System.Windows.MessageBox.Show(
                this,
                "无效的地址格式，请输入十六进制地址。",
                "Call",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        ulong[] arguments = new ulong[8];
        if (!TryReadArgument(Param1Box.Text, 0, arguments) ||
            !TryReadArgument(Param2Box.Text, 1, arguments) ||
            !TryReadArgument(Param3Box.Text, 2, arguments) ||
            !TryReadArgument(Param4Box.Text, 3, arguments) ||
            !TryReadArgument(Param5Box.Text, 4, arguments) ||
            !TryReadArgument(Param6Box.Text, 5, arguments) ||
            !TryReadArgument(Param7Box.Text, 6, arguments) ||
            !TryReadArgument(Param8Box.Text, 7, arguments))
        {
            System.Windows.MessageBox.Show(
                this,
                "参数格式无效。",
                "Call",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        ExecuteButton.IsEnabled = false;
        try
        {
            RemoteOperationResult result = await _viewModel.CallRemoteFunction64Async(
                functionAddress,
                arguments,
                creationFlags: 0);
            if (!result.Succeeded)
            {
                System.Windows.MessageBox.Show(
                    this,
                    result.ErrorMessage,
                    "Call",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            ReturnValueBox.Text = $"0x{result.ReturnValue:X}";
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                this,
                exception.Message,
                "Call",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            ExecuteButton.IsEnabled = true;
        }
    }

    private static bool TryReadArgument(
        string text,
        int index,
        ulong[] arguments)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            arguments[index] = 0;
            return true;
        }

        string value = text.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(
                value[2..],
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out arguments[index]);
        }

        return ulong.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out arguments[index]);
    }

    private static bool TryParseHex(string text, out ulong value)
    {
        string normalized = text.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }

        return ulong.TryParse(
            normalized,
            NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture,
            out value);
    }

    private void OnCloseClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        Close();
    }
}
