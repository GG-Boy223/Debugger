using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DogeDebugger.UI.Views.Dialogs;

public partial class ConditionBreakpointDialog : Window
{
    private readonly Func<string, string?> _validator;
    private readonly IReadOnlyList<(string Category, string[] Examples)> _examples =
    [
        (
            "寄存器比较",
            [
                "RAX == 0x1234",
                "RCX ~= 0",
                "RDX > 0x100 and RDX < 0x200"
            ]),
        (
            "标志位判断",
            [
                "ZF == 1",
                "CF == 0 and OF == 0"
            ]),
        (
            "内存读取",
            [
                "readInteger(RSP + 8) == 42",
                "readPointer(RCX) ~= 0",
                "readString(RDX, 10) == \"hello\""
            ]),
        (
            "位运算",
            [
                "RAX % 2 == 0",
                "EFLAGS % 128 >= 64"
            ]),
        (
            "复合条件",
            [
                "(RAX > 0 and RCX < 100) or ZF == 1"
            ]),
        (
            "计数器技巧",
            [
                "counter = (counter or 0) + 1; return counter >= 100"
            ])
    ];

    public ConditionBreakpointDialog(
        ulong address,
        string? existingCondition,
        Func<string, string?> validator)
    {
        InitializeComponent();
        HeaderText.Text = $"条件断点 — 0x{address:X}";
        ConditionBox.Text = existingCondition ?? string.Empty;
        _validator = validator;
        BuildExamples();
        Loaded += OnLoaded;
    }

    public string Condition => ConditionBox.Text.Trim();

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        ConditionBox.Focus();
        ConditionBox.SelectAll();
    }

    private void BuildExamples()
    {
        foreach ((string category, string[] examples) in _examples)
        {
            TextBlock categoryText = new()
            {
                Text = category,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, ExampleList.Children.Count > 0 ? 8 : 0, 0, 4)
            };
            ExampleList.Children.Add(categoryText);

            foreach (string example in examples)
            {
                TextBlock exampleText = new()
                {
                    Text = example,
                    FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
                    FontSize = 11,
                    Padding = new Thickness(8, 1, 0, 1),
                    Cursor = Cursors.Hand,
                    ToolTip = "单击填入编辑框"
                };
                exampleText.MouseLeftButtonUp += (_, _) =>
                {
                    ConditionBox.Text = example;
                    ConditionBox.CaretIndex = ConditionBox.Text.Length;
                    ConditionBox.Focus();
                };
                ExampleList.Children.Add(exampleText);
            }
        }

        TextBlock apiText = new()
        {
            Margin = new Thickness(0, 10, 0, 0),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(96, 96, 96)),
            Text =
                "可用寄存器: RAX RBX RCX RDX RSI RDI RBP RSP R8-R15 RIP EAX-ESP EIP\n" +
                "可用标志位: CF PF AF ZF SF TF DF OF EFLAGS RFLAGS\n" +
                "可用 API: readInteger readQword readFloat readDouble readPointer readString readBytes\n" +
                "Lua 逻辑: and  or  not  ~= (不等于)  == (等于)  返回 true 暂停 / false 继续"
        };
        ExampleList.Children.Add(apiText);
    }

    private void OnConditionTextChanged(
        object sender,
        TextChangedEventArgs eventArgs)
    {
        ValidationText.Visibility = Visibility.Collapsed;
    }

    private void OnExamplesExpanded(
        object sender,
        RoutedEventArgs eventArgs)
    {
        Height = 581;
    }

    private void OnExamplesCollapsed(
        object sender,
        RoutedEventArgs eventArgs)
    {
        Height = 280;
    }

    private void OnValidateClick(object sender, RoutedEventArgs eventArgs)
    {
        ShowValidation(_validator(ConditionBox.Text.Trim()));
    }

    private bool ShowValidation(string? error)
    {
        bool valid = string.IsNullOrWhiteSpace(error);
        ValidationText.Text = valid ? "语法正确" : $"错误: {error}";
        ValidationText.Foreground = new SolidColorBrush(
            valid ? Color.FromRgb(46, 204, 113) : Color.FromRgb(231, 76, 60));
        ValidationText.Visibility = Visibility.Visible;
        return valid;
    }

    private void OnClearClick(object sender, RoutedEventArgs eventArgs)
    {
        ConditionBox.Clear();
        ValidationText.Visibility = Visibility.Collapsed;
        ConditionBox.Focus();
    }

    private void OnOkClick(object sender, RoutedEventArgs eventArgs)
    {
        string condition = ConditionBox.Text.Trim();
        if (condition.Length > 0 && !ShowValidation(_validator(condition)))
        {
            return;
        }

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs eventArgs)
    {
        DialogResult = false;
    }
}
