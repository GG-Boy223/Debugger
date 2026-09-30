using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DogeDebugger.Core.Search;

namespace DogeDebugger.UI.Views.Dialogs;

public partial class PointerScanSettingsDialog : Window
{
    public PointerScanSettingsDialog()
    {
        InitializeComponent();
    }

    public PointerScanSettingsDialog(
        PointerScanOptions options,
        bool rescanMode,
        ulong? rescanTarget = null)
        : this()
    {
        TargetAddressBox.Text = options.TargetAddress.ToString("X");
        MaxDepthBox.Text = options.MaximumDepth.ToString(CultureInfo.InvariantCulture);
        MaxOffsetBox.Text = options.MaximumOffset.ToString("X");
        MaxResultsBox.Text = options.MaximumResults.ToString(CultureInfo.InvariantCulture);
        LimitOffsetsPerNodeCheck.IsChecked = options.LimitOffsetsPerNode;
        MaxOffsetsPerNodeBox.Text = options.MaximumOffsetsPerNode.ToString(
            CultureInfo.InvariantCulture);
        StaticOnlyBaseCheck.IsChecked = options.StaticOnlyBase;
        IncludeMappedMemoryCheck.IsChecked = options.IncludeMappedMemory;
        AllowNegativeOffsetsCheck.IsChecked = options.AllowNegativeOffsets;
        AlignmentCombo.SelectedIndex = options.Alignment switch
        {
            1 => 0,
            8 => 2,
            _ => 1
        };
        SetRescanMode(rescanMode, rescanTarget ?? options.TargetAddress);
    }

    public PointerScanOptions? Options { get; private set; }

    public PointerScanRescanOptions? RescanOptions { get; private set; }

    private void SetRescanMode(bool enabled, ulong target)
    {
        Visibility visibility = enabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        RescanSeparator.Visibility = visibility;
        RescanTitle.Visibility = visibility;
        RescanModePanel.Visibility = visibility;
        RescanTargetPanel.Visibility = visibility;
        if (enabled)
        {
            RescanTargetBox.Text = target.ToString("X");
        }
    }

    private void OnConfirmClick(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            Options = BuildOptions();
            if (RescanTargetPanel.Visibility == Visibility.Visible)
            {
                RescanOptions = BuildRescanOptions();
            }

            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"参数错误：{exception.Message}",
                "错误",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private PointerScanOptions BuildOptions()
    {
        return new PointerScanOptions
        {
            TargetAddress = ParseHex(
                TargetAddressBox.Text,
                "目标地址"),
            MaximumDepth = ParsePositiveInt(
                MaxDepthBox.Text,
                "最大深度"),
            MaximumOffset = checked((int)ParseHex(
                MaxOffsetBox.Text,
                "最大偏移")),
            Alignment = GetSelectedAlignment(),
            MaximumResults = ParsePositiveInt(
                MaxResultsBox.Text,
                "最大结果数"),
            MaximumOffsetsPerNode = LimitOffsetsPerNodeCheck.IsChecked == true
                ? ParsePositiveInt(
                    MaxOffsetsPerNodeBox.Text,
                    "每节点最大偏移变体数")
                : 3,
            LimitOffsetsPerNode = LimitOffsetsPerNodeCheck.IsChecked == true,
            StaticOnlyBase = StaticOnlyBaseCheck.IsChecked == true,
            IncludeMappedMemory = IncludeMappedMemoryCheck.IsChecked == true,
            AllowNegativeOffsets = AllowNegativeOffsetsCheck.IsChecked == true
        };
    }

    private PointerScanRescanOptions BuildRescanOptions()
    {
        PointerScanRescanMode mode = RescanModeCombo.SelectedIndex == 1
            ? PointerScanRescanMode.Value
            : PointerScanRescanMode.Address;
        return new PointerScanRescanOptions
        {
            Mode = mode,
            Target = ParseHex(
                RescanTargetBox.Text,
                mode == PointerScanRescanMode.Address
                    ? "重扫描地址"
                    : "重扫描数值"),
            ValueSize = RescanValueTypeCombo.SelectedIndex switch
            {
                0 => 1,
                1 => 2,
                2 => 4,
                3 => 8,
                _ => 4
            }
        };
    }

    private int GetSelectedAlignment()
    {
        if (AlignmentCombo.SelectedItem is ComboBoxItem { Tag: string tag } &&
            int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            return value;
        }

        throw new FormatException("指针对齐无效。");
    }

    private static int ParsePositiveInt(string text, string fieldName)
    {
        if (!int.TryParse(
                text.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int value) ||
            value <= 0)
        {
            throw new FormatException($"{fieldName}必须是正整数。");
        }

        return value;
    }

    private static ulong ParseHex(string text, string fieldName)
    {
        string value = text.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        if (value.Length == 0 ||
            !ulong.TryParse(
                value,
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out ulong result))
        {
            throw new FormatException($"{fieldName}必须是十六进制值。");
        }

        return result;
    }

    private void OnCancelClick(object sender, RoutedEventArgs eventArgs)
    {
        DialogResult = false;
    }
}
