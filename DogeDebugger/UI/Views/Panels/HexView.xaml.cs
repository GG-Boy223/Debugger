using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DogeDebugger.Core.Modules;
using DogeDebugger.Debugger.Breakpoints;
using DogeDebugger.UI.Views.Dialogs;

namespace DogeDebugger.UI.Views.Panels;

public partial class HexView : UserControl
{
    public HexView()
    {
        InitializeComponent();
        HexTabs.ItemsSource = Enumerable.Range(1, 6).ToArray();
        HexTabs.SelectedIndex = 0;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private HexViewLine? SelectedLine =>
        (HexTabs.SelectedIndex >= 0 ? HexGrid.SelectedItem : null) as HexViewLine ??
        HexGrid.SelectedItem as HexViewLine;

    private void OnHexGridPreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.OriginalSource is not DependencyObject source)
        {
            return;
        }

        DataGridRow? row = ItemsControl.ContainerFromElement(HexGrid, source)
            as DataGridRow;
        if (row is not null)
        {
            HexGrid.SelectedItem = row.Item;
        }
    }

    private void OnHexContextMenuOpened(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (sender is not ContextMenu menu || ViewModel is not { } viewModel)
        {
            return;
        }

        SetChecked(menu, "十六进制 (Hex)", viewModel.HexDisplayMode == HexDisplayMode.Hex);
        SetChecked(menu, "单字节", false);
        SetChecked(menu, "双字节", viewModel.HexDisplayMode == HexDisplayMode.Word);
        SetChecked(menu, "四字节", viewModel.HexDisplayMode == HexDisplayMode.Dword);
        SetChecked(menu, "八字节", viewModel.HexDisplayMode == HexDisplayMode.Qword);
        SetChecked(menu, "浮点数 (Float)", viewModel.HexDisplayMode == HexDisplayMode.Float);
        SetChecked(menu, "双浮点 (Double)", viewModel.HexDisplayMode == HexDisplayMode.Double);

        foreach (int count in new[] { 4, 8, 16, 24, 32 })
        {
            SetChecked(
                menu,
                $"{count} 字节",
                viewModel.HexBytesPerRow == count);
        }

        foreach (string encoding in new[] { "ASCII", "UTF-8", "UTF-16", "GBK", "BIG5" })
        {
            SetChecked(
                menu,
                encoding,
                string.Equals(
                    viewModel.HexTextEncoding,
                    encoding,
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    private async void OnHexGoToAddressClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        TextInputDialog dialog = new(
            "跳转到地址",
            "输入地址、模块+偏移或符号：",
            SelectedLine is { } line ? $"0x{line.Address:X}" : "0x")
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true)
        {
            await viewModel.NavigateToAddressExpressionAsync(dialog.Value);
        }
    }

    private void OnHexDisplayModeClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel ||
            sender is not MenuItem { Tag: string tag } ||
            !Enum.TryParse(tag, out HexDisplayMode mode))
        {
            return;
        }

        viewModel.HexDisplayMode = mode;
    }

    private void OnHexBytesPerRowClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is { } viewModel &&
            sender is MenuItem { Tag: string tag } &&
            int.TryParse(tag, out int count))
        {
            viewModel.HexBytesPerRow = Math.Clamp(count, 4, 32);
        }
    }

    private void OnHexTextEncodingClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is { } viewModel &&
            sender is MenuItem { Tag: string encoding })
        {
            viewModel.HexTextEncoding = encoding;
        }
    }

    private void OnHexHardwareBreakpointClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel ||
            SelectedLine is not { } line ||
            sender is not MenuItem { Tag: string kindText } ||
            !Enum.TryParse(kindText, out BreakpointKind kind))
        {
            return;
        }

        viewModel.AddHardwareBreakpointAtAddress(line.Address, kind);
    }

    private void OnHexViewAccessRecordClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel ||
            sender is not MenuItem { Tag: string kind })
        {
            return;
        }

        bool write = string.Equals(
            kind,
            "Write",
            StringComparison.OrdinalIgnoreCase);
        HardwareAccessRecord[] rows = viewModel.HardwareAccessRecords
            .Where(record => write
                ? record.Kind == BreakpointKind.HardwareWrite
                : record.Kind != BreakpointKind.HardwareWrite)
            .Take(200)
            .ToArray();
        string text = rows.Length == 0
            ? "暂无记录。"
            : string.Join(
                Environment.NewLine,
                rows.Select(record =>
                    $"{record.TimestampText}\t{record.AddressText}\t{record.KindText}\tTID {record.ThreadId}"));
        MessageBox.Show(
            Window.GetWindow(this),
            text,
            write ? "查看写入记录" : "查看读取记录",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OnHexChangeProtectionClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel ||
            SelectedLine is not { } line)
        {
            return;
        }

        Core.Memory.MemoryRegionInfo? region = viewModel.MemoryRegions
            .FirstOrDefault(item =>
                line.Address >= item.BaseAddress &&
                line.Address < item.BaseAddress + item.Size);
        if (region is null)
        {
            return;
        }

        TextInputDialog dialog = new(
            "修改内存属性",
            $"区域 0x{region.BaseAddress:X} 大小 0x{region.Size:X}，当前保护 0x{region.Protect:X}。输入新保护值（十六进制）：",
            $"0x{region.Protect:X}",
            width: 520)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string value = dialog.Value.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        if (uint.TryParse(
                value,
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out uint protection))
        {
            viewModel.ChangeMemoryProtection(line.Address, protection);
        }
    }

    private void OnHexCopyClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel ||
            SelectedLine is not { } line ||
            sender is not MenuItem { Tag: string copyKind })
        {
            return;
        }

        ModuleDescriptor? module = viewModel.FindModuleForAddress(line.Address);
        string text = copyKind switch
        {
            "Rva" when module is not null =>
                $"0x{line.Address - module.BaseAddress:X}",
            "Line" =>
                $"{line.AddressText}  {line.Hex}  {line.Ascii}",
            _ => module is null
                ? $"0x{line.Address:X}"
                : $"{module.Name}+{line.Address - module.BaseAddress:X}"
        };
        try
        {
            Clipboard.SetText(text);
            viewModel.StatusText = "已复制到剪贴板。";
        }
        catch (Exception exception)
        {
            viewModel.StatusText = exception.Message;
        }
    }

    private static MenuItem? FindMenuItem(
        ItemsControl root,
        string headerPrefix)
    {
        foreach (object item in root.Items)
        {
            if (item is not MenuItem menuItem)
            {
                continue;
            }

            if (menuItem.Header?.ToString()?.StartsWith(
                    headerPrefix,
                    StringComparison.Ordinal) == true)
            {
                return menuItem;
            }

            MenuItem? nested = FindMenuItem(menuItem, headerPrefix);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static void SetChecked(
        ContextMenu menu,
        string headerPrefix,
        bool isChecked)
    {
        MenuItem? item = FindMenuItem(menu, headerPrefix);
        if (item is not null)
        {
            item.IsCheckable = true;
            item.IsChecked = isChecked;
        }
    }
}
