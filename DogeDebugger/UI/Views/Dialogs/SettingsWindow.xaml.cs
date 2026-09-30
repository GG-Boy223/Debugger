using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DogeDebugger.Core.Settings;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using DataGrid = System.Windows.Controls.DataGrid;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace DogeDebugger.UI.Views.Dialogs;

public partial class SettingsWindow : FluentWindow
{
    private readonly SettingsStore _settings;
    private readonly AppSettings _workingCopy;

    public SettingsWindow(SettingsStore settings, string initialPage = "常规")
    {
        _settings = settings;
        _workingCopy = settings.Current.Clone();
        InitializeComponent();

        NavigationList.ItemsSource = CreateNavigationItems();
        ShortcutList.ItemsSource = CreateShortcutItems();
        LoadGeneralPage();

        SettingsNavigationItem item = NavigationList.Items
            .OfType<SettingsNavigationItem>()
            .FirstOrDefault(candidate => candidate.Title == initialPage)
            ?? NavigationList.Items.OfType<SettingsNavigationItem>().First();
        NavigationList.SelectedItem = item;
    }

    private static IReadOnlyList<SettingsNavigationItem> CreateNavigationItems() =>
    [
        new("常规", "Settings20", true),
        new("断点", "Bug20", true),
        new("调试", "Bug20", true),
        new("源码调试", "Code20", true),
        new("异常处理", "Shield20", true),
        new("快捷键", "Keyboard20", true),
        new("外观", "PaintBrush20", true),
        new("AI", "Sparkle20", true),
        new("MCP", "PlugConnected20", true),
        new("外部 MCP", "PlugDisconnected20", true)
    ];

    private static IReadOnlyList<ShortcutDisplayItem> CreateShortcutItems() =>
    [
        new("运行", "F9", "继续执行目标进程"),
        new("不处理异常", "Shift+F9", "把异常交还目标程序"),
        new("运行到光标", "F4", "运行到选中的反汇编地址"),
        new("暂停", "F12", "中断目标进程"),
        new("步入", "F7", "执行一条指令并进入调用"),
        new("步过", "F8", "执行一条指令并跳过调用"),
        new("步出", "Ctrl+F9", "执行到当前函数返回"),
        new("切换断点", "F2", "设置或移除软件断点"),
        new("重新开始", "Ctrl+F2", "使用相同参数重新启动"),
        new("脱离", "Ctrl+Alt+F2", "停止调试并脱离目标"),
        new("打开进程", "Ctrl+P", "打开进程选择对话框"),
        new("附加到进程", "Alt+A", "附加当前选中进程"),
        new("启动调试", "F3", "启动并调试命令行目标"),
        new("退出", "Alt+X", "关闭 DogeDebugger"),
        new("下一个面板", "Ctrl+Tab", "切换到下一个文档标签"),
        new("上一个面板", "Ctrl+Shift+Tab", "切换到上一个文档标签"),
        new("关闭当前面板", "Ctrl+W", "隐藏当前面板"),
        new("首次搜索", "Ctrl+Shift+S", "执行内存首次扫描"),
        new("再次搜索", "Ctrl+Shift+N", "继续筛选内存结果"),
        new("运行 Lua", "Ctrl+Shift+F11", "执行 Lua 并调用 OnStart"),
        new("停止 Lua", "Ctrl+Shift+F12", "调用 OnEnd 并停止 Lua"),
        new("快捷键速查", "Ctrl+Shift+/", "显示快捷键速查层")
    ];

    private void LoadGeneralPage()
    {
        AskClearListToggle.IsChecked = _workingCopy.AskClearListOnNewProcess;
        SymbolServerTextBox.Text = _workingCopy.SymbolServerUrl;
        SaveWindowLayoutToggle.IsChecked = _workingCopy.SaveWindowLayout;
        ShowScriptConsoleToggle.IsChecked = _workingCopy.ShowScriptConsole;
        ValueLockIntervalNumberBox.Value = _workingCopy.ValueLockIntervalMs;
    }

    private void OnNavigationSelectionChanged(
        object sender,
        SelectionChangedEventArgs eventArgs)
    {
        if (eventArgs.AddedItems.Count == 0 ||
            eventArgs.AddedItems[0] is not SettingsNavigationItem item)
        {
            return;
        }

        GeneralPage.Visibility = item.Title == "常规"
            ? Visibility.Visible
            : Visibility.Collapsed;
        ShortcutPage.Visibility = item.Title == "快捷键"
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (item.Title is "断点" or "调试" or "源码调试" or "异常处理" or
            "外观" or "AI" or "MCP" or "外部 MCP")
        {
            BuildDynamicPage(item.Title);
        }
        else
        {
            DynamicPage.Visibility = Visibility.Collapsed;
        }
    }

    private void BuildDynamicPage(string title)
    {
        DynamicPageContent.Children.Clear();
        DynamicPageContent.Children.Add(new TextBlock
        {
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Text = title
        });

        switch (title)
        {
            case "断点":
                BuildBreakpointPage();
                break;
            case "调试":
                BuildDebugPage();
                break;
            case "源码调试":
                BuildSourceDebugPage();
                break;
            case "异常处理":
                BuildExceptionPage();
                break;
            case "外观":
                BuildAppearancePage();
                break;
            case "AI":
                BuildAiPage();
                break;
            case "MCP":
                BuildMcpPage();
                break;
            case "外部 MCP":
                BuildExternalMcpPage();
                break;
        }

        DynamicPage.Visibility = Visibility.Visible;
    }

    private void BuildBreakpointPage()
    {
        AddSection("断点行为");
        AddToggle(
            "切换断点时删除断点",
            "再次切换已存在的断点时直接删除，而不是禁用。",
            nameof(AppSettings.DeleteBreakpointOnToggle));

        AddSection("调试事件");
        AddToggle("系统断点", "首次附加到进程时中断。", "DebugEvents.SystemBreakpoint");
        AddToggle("入口断点", "在目标入口点中断。", "DebugEvents.EntryBreakpoint");
        AddToggle("NtTerminateProcess", "在目标进程终止调用处中断。", "DebugEvents.NtTerminateProcess");
        AddToggle("TLS 回调", "在 TLS 回调执行时中断。", "DebugEvents.TlsCallbacks");
        AddToggle("系统 TLS 回调", "包含系统模块 TLS 回调。", "DebugEvents.TlsCallbacksSystem");
        AddToggle("线程入口", "在线程入口点中断。", "DebugEvents.ThreadEntry");
        AddToggle("线程开始", "记录线程开始事件。", "DebugEvents.ThreadStart");
        AddToggle("线程结束", "记录线程结束事件。", "DebugEvents.ThreadEnd");
        AddToggle("线程名称设置", "记录线程名称变更事件。", "DebugEvents.ThreadNameSet");
        AddToggle("DLL 入口", "在 DLL 入口点中断。", "DebugEvents.DllEntry");
        AddToggle("系统 DLL 入口", "包含系统模块 DLL 入口。", "DebugEvents.DllEntrySystem");
        AddToggle("DLL 加载", "记录 DLL 加载事件。", "DebugEvents.DllLoad");
        AddToggle("系统 DLL 加载", "包含系统模块 DLL 加载。", "DebugEvents.DllLoadSystem");
        AddToggle("DLL 卸载", "记录 DLL 卸载事件。", "DebugEvents.DllUnload");
        AddToggle("系统 DLL 卸载", "包含系统模块 DLL 卸载。", "DebugEvents.DllUnloadSystem");
        AddToggle("调试字符串", "记录 OutputDebugString 字符串。", "DebugEvents.DebugStrings");
    }

    private void BuildDebugPage()
    {
        AddSection("步进");
        AddToggle(
            "步进限制在当前线程",
            "单步时只在当前线程上等待事件，减少其他线程干扰。",
            nameof(AppSettings.RestrictStepToCurrentThread));

        AddSection("保存断点");
        AddCombo(
            "启动时恢复保存的断点",
            "选择恢复保存断点时的启用状态。",
            nameof(AppSettings.SavedBreakpointRestoreMode),
            [
                new OptionItem(0, "全部禁用"),
                new OptionItem(1, "按上次状态启用"),
                new OptionItem(2, "全部启用")
            ]);
        AddToggle(
            "启动后自动启用保存的断点",
            "目标启动后自动恢复并启用保存的断点。",
            nameof(AppSettings.AutoEnableSavedBreakpointsOnLaunch));

        AddSection("自动附加");
        AddText(
            "自动附加进程名",
            "填写进程名列表，多个名称使用换行或分号分隔。",
            nameof(AppSettings.AutoAttachProcessNames));
        AddToggle(
            "已选择进程时也允许自动附加",
            "即使当前已有目标进程，也允许自动附加新进程。",
            nameof(AppSettings.AutoAttachAllowWhenProcessSelected));
        AddNumber(
            "自动附加检查间隔",
            "轮询目标进程列表的时间间隔。",
            nameof(AppSettings.AutoAttachIntervalMs),
            100,
            60000,
            "ms");
        AddCombo(
            "自动附加时处理保存地址",
            "自动附加到新进程时如何处理保存的地址列表。",
            nameof(AppSettings.AutoAttachSavedAddressMode),
            [
                new OptionItem(0, "询问"),
                new OptionItem(1, "保留"),
                new OptionItem(2, "清空")
            ]);
    }

    private void BuildSourceDebugPage()
    {
        AddSection("源码与符号");
        AddToggle(
            "启用源码调试",
            "加载 PDB 并将指令地址映射到源文件和行号。",
            "SourceDebugging.Enabled");
        AddText(
            "源码根目录",
            "源码文件的根目录。",
            "SourceDebugging.SourceRoot",
            () => BrowseFolder(SourceDebugRootTextBox, "选择源码根目录"));
        AddText(
            "PDB 根目录",
            "PDB 文件缓存或原始 PDB 的根目录。",
            "SourceDebugging.PdbRoot",
            () => BrowseFolder(SourceDebugPdbRootTextBox, "选择 PDB 根目录"));
    }

    private void BuildExceptionPage()
    {
        AddSection("异常处理模式");
        AddCombo(
            "异常处理模式",
            "选择所有异常的处理方式，或使用自定义规则。",
            "ExceptionHandling.Mode",
            [
                new OptionItem(0, "全部中断"),
                new OptionItem(1, "全部传递"),
                new OptionItem(2, "自定义规则")
            ]);
        AddToggle(
            "记录非调试器异常",
            "记录不会传给调试器的异常事件。",
            "ExceptionHandling.LogNonDebuggerExceptions");

        AddSection("自定义规则");
        DataGrid grid = new()
        {
            Height = 240,
            AutoGenerateColumns = false,
            CanUserAddRows = true,
            CanUserDeleteRows = true,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal
        };
        grid.SetBinding(
            ItemsControl.ItemsSourceProperty,
            new Binding("ExceptionHandling.CustomRules")
            {
                Source = _workingCopy,
                Mode = BindingMode.TwoWay
            });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "异常代码",
            Width = 130,
            Binding = new Binding(nameof(ExceptionRule.ExceptionCode))
            {
                StringFormat = "0x{0:X8}"
            }
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "名称",
            Width = 220,
            Binding = new Binding(nameof(ExceptionRule.Name))
        });
        grid.Columns.Add(new DataGridComboBoxColumn
        {
            Header = "动作",
            Width = 150,
            ItemsSource = Enum.GetValues<ExceptionAction>(),
            SelectedValueBinding = new Binding(nameof(ExceptionRule.Action))
        });
        DynamicPageContent.Children.Add(grid);
    }

    private void BuildAppearancePage()
    {
        AddSection("主题");
        AddStringCombo(
            "主题模式",
            "选择浅色或深色主题。",
            nameof(AppSettings.ThemeMode),
            [
                new StringOptionItem("Light", "浅色"),
                new StringOptionItem("Dark", "深色")
            ]);

        AddSection("字体");
        AddText(
            "代码字体",
            "反汇编、十六进制和脚本编辑器使用的字体。",
            nameof(AppSettings.CodeFontFamily));
        AddNumber(
            "代码字号",
            "代码文本字号。",
            nameof(AppSettings.CodeFontSize),
            8,
            48,
            "pt");
        AddText(
            "界面字体",
            "主窗口和对话框使用的字体。",
            nameof(AppSettings.UiFontFamily));
        AddNumber(
            "界面字号",
            "界面文本字号。",
            nameof(AppSettings.UiFontSize),
            8,
            32,
            "pt");
    }

    private void BuildAiPage()
    {
        AddSection("接口");
        AddText("API 地址", "兼容 OpenAI 接口的基础地址。", nameof(AppSettings.AiApiUrl));
        AddText("API 密钥", "用于请求 AI 服务的密钥。", nameof(AppSettings.AiApiKey));
        AddText("默认模型", "聊天请求默认使用的模型名称。", nameof(AppSettings.AiDefaultModel));
        AddText("推理强度", "模型支持的 reasoning effort 值。", nameof(AppSettings.AiReasoningEffort));
        AddNumber(
            "温度",
            "采样温度，值越高输出越随机。",
            nameof(AppSettings.AiTemperature),
            0,
            2,
            string.Empty);

        AddSection("交互");
        AddToggle(
            "按 Enter 发送",
            "输入框按 Enter 时直接发送消息。",
            nameof(AppSettings.AiSendOnEnter));
        AddToggle(
            "显示推理内容",
            "显示模型返回的推理过程。",
            nameof(AppSettings.AiShowReasoning));
        AddToggle(
            "长度截断后自动继续",
            "模型因长度停止时自动发送继续请求。",
            nameof(AppSettings.AiAutoContinueOnLength));
        AddToggle(
            "包含内置指导",
            "在系统提示词中包含 DogeDebugger 内置指导。",
            nameof(AppSettings.AiIncludeBuiltInGuidelines));
        AddToggle(
            "只读模式",
            "禁止 AI 修改目标进程和调试器状态。",
            nameof(AppSettings.AiReadOnlyMode));

        AddSection("系统提示词");
        AddTextArea(
            "系统提示词",
            "每次对话开始时发送给模型的系统消息。",
            nameof(AppSettings.AiSystemPrompt));
    }

    private void BuildMcpPage()
    {
        AddSection("内置 MCP 服务");
        AddToggle("启用 MCP", "启动 DogeDebugger 内置 MCP 服务。", nameof(AppSettings.McpEnabled));
        AddText(
            "监听地址",
            "MCP HTTP 服务绑定的本地地址。",
            nameof(AppSettings.McpListenAddress));
        AddNumber(
            "监听端口",
            "MCP HTTP 服务使用的端口。",
            nameof(AppSettings.McpPort),
            1,
            65535,
            string.Empty);
        AddToggle(
            "隐私保护",
            "在工具输出中遮蔽敏感路径和进程信息。",
            nameof(AppSettings.McpPrivacyProtectionEnabled));
        AddNumber(
            "输出配置",
            "MCP 工具输出详细程度配置值。",
            nameof(AppSettings.McpOutputProfile),
            0,
            2,
            string.Empty);
    }

    private void BuildExternalMcpPage()
    {
        AddSection("外部 MCP 服务");
        AddToggle(
            "启用外部 MCP",
            "允许 DogeDebugger 连接外部 MCP 服务。",
            nameof(AppSettings.ExternalMcpEnabled));

        DataGrid grid = new()
        {
            Height = 380,
            AutoGenerateColumns = false,
            CanUserAddRows = true,
            CanUserDeleteRows = true,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal
        };
        grid.SetBinding(
            ItemsControl.ItemsSourceProperty,
            new Binding(nameof(AppSettings.McpServers))
            {
                Source = _workingCopy,
                Mode = BindingMode.TwoWay
            });
        grid.Columns.Add(new DataGridCheckBoxColumn
        {
            Header = "启用",
            Width = 60,
            Binding = new Binding(nameof(McpServerConfig.Enabled))
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "名称",
            Width = 180,
            Binding = new Binding(nameof(McpServerConfig.Name))
        });
        grid.Columns.Add(new DataGridComboBoxColumn
        {
            Header = "传输",
            Width = 130,
            ItemsSource = Enum.GetValues<McpTransportKind>(),
            SelectedValueBinding = new Binding(nameof(McpServerConfig.Transport))
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "命令",
            Width = 220,
            Binding = new Binding(nameof(McpServerConfig.Command))
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "URL",
            Width = 260,
            Binding = new Binding(nameof(McpServerConfig.Url))
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "工作目录",
            Width = 240,
            Binding = new Binding(nameof(McpServerConfig.WorkingDirectory))
        });
        DynamicPageContent.Children.Add(grid);
    }

    private void AddSection(string title)
    {
        DynamicPageContent.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 18, 0, 0),
            FontWeight = FontWeights.SemiBold,
            Text = title
        });
    }

    private void AddToggle(string title, string description, string path)
    {
        Wpf.Ui.Controls.ToggleSwitch toggle = new();
        toggle.SetBinding(
            Wpf.Ui.Controls.ToggleSwitch.IsCheckedProperty,
            new Binding(path)
            {
                Source = _workingCopy,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
        DynamicPageContent.Children.Add(CreateCard(title, description, toggle));
    }

    private System.Windows.Controls.TextBox? SourceDebugRootTextBox;
    private System.Windows.Controls.TextBox? SourceDebugPdbRootTextBox;

    private void AddText(
        string title,
        string description,
        string path,
        Action? browse = null)
    {
        System.Windows.Controls.TextBox textBox = new()
        {
            Width = 430,
            Height = 30,
            VerticalAlignment = VerticalAlignment.Center
        };
        textBox.SetBinding(
            TextBox.TextProperty,
            new Binding(path)
            {
                Source = _workingCopy,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
        if (path == "SourceDebugging.SourceRoot")
        {
            SourceDebugRootTextBox = textBox;
        }
        else if (path == "SourceDebugging.PdbRoot")
        {
            SourceDebugPdbRootTextBox = textBox;
        }

        Grid editor = new();
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(textBox, 0);
        editor.Children.Add(textBox);
        if (browse is not null)
        {
            Wpf.Ui.Controls.Button button = new()
            {
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(10, 3, 10, 3),
                Content = "浏览..."
            };
            button.Click += (_, _) => browse();
            Grid.SetColumn(button, 1);
            editor.Children.Add(button);
        }

        DynamicPageContent.Children.Add(CreateCard(title, description, editor));
    }

    private void AddTextArea(string title, string description, string path)
    {
        TextBox textBox = new()
        {
            Width = 620,
            Height = 110,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        textBox.SetBinding(
            TextBox.TextProperty,
            new Binding(path)
            {
                Source = _workingCopy,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
        DynamicPageContent.Children.Add(CreateCard(title, description, textBox));
    }

    private void AddNumber(
        string title,
        string description,
        string path,
        double minimum,
        double maximum,
        string suffix)
    {
        Wpf.Ui.Controls.NumberBox numberBox = new()
        {
            Width = 150,
            Height = 30,
            Minimum = minimum,
            Maximum = maximum,
            SmallChange = 1,
            SpinButtonPlacementMode = Wpf.Ui.Controls.NumberBoxSpinButtonPlacementMode.Compact
        };
        numberBox.SetBinding(
            Wpf.Ui.Controls.NumberBox.ValueProperty,
            new Binding(path)
            {
                Source = _workingCopy,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
        StackPanel editor = new() { Orientation = Orientation.Horizontal };
        editor.Children.Add(numberBox);
        editor.Children.Add(new TextBlock
        {
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Text = suffix
        });
        DynamicPageContent.Children.Add(CreateCard(title, description, editor));
    }

    private void AddCombo(
        string title,
        string description,
        string path,
        IReadOnlyList<OptionItem> options)
    {
        ComboBox comboBox = new()
        {
            Width = 220,
            Height = 30,
            ItemsSource = options,
            DisplayMemberPath = nameof(OptionItem.Label),
            SelectedValuePath = nameof(OptionItem.Value)
        };
        comboBox.SetBinding(
            ComboBox.SelectedValueProperty,
            new Binding(path)
            {
                Source = _workingCopy,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
        DynamicPageContent.Children.Add(CreateCard(title, description, comboBox));
    }

    private void AddStringCombo(
        string title,
        string description,
        string path,
        IReadOnlyList<StringOptionItem> options)
    {
        ComboBox comboBox = new()
        {
            Width = 220,
            Height = 30,
            ItemsSource = options,
            DisplayMemberPath = nameof(StringOptionItem.Label),
            SelectedValuePath = nameof(StringOptionItem.Value)
        };
        comboBox.SetBinding(
            ComboBox.SelectedValueProperty,
            new Binding(path)
            {
                Source = _workingCopy,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
        DynamicPageContent.Children.Add(CreateCard(title, description, comboBox));
    }

    private static Border CreateCard(string title, string description, UIElement editor)
    {
        Grid grid = new();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        StackPanel text = new();
        text.Children.Add(new TextBlock { Text = title });
        text.Children.Add(new TextBlock
        {
            Foreground = System.Windows.Media.Brushes.Gray,
            FontSize = 11,
            Text = description
        });
        Grid.SetColumn(text, 0);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(text);
        grid.Children.Add(editor);

        return new Border
        {
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(12),
            Background = System.Windows.Media.Brushes.White,
            BorderBrush = System.Windows.Media.Brushes.LightGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = grid
        };
    }

    private void BrowseFolder(System.Windows.Controls.TextBox? textBox, string title)
    {
        if (textBox is null)
        {
            return;
        }

        OpenFolderDialog dialog = new()
        {
            Title = title,
            InitialDirectory = textBox.Text
        };
        if (dialog.ShowDialog(this) == true)
        {
            textBox.Text = dialog.FolderName;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs eventArgs)
    {
        _workingCopy.AskClearListOnNewProcess = AskClearListToggle.IsChecked == true;
        _workingCopy.SymbolServerUrl = SymbolServerTextBox.Text.Trim();
        _workingCopy.SaveWindowLayout = SaveWindowLayoutToggle.IsChecked == true;
        _workingCopy.ShowScriptConsole = ShowScriptConsoleToggle.IsChecked == true;
        _workingCopy.ValueLockIntervalMs = Math.Max(
            1,
            (int)Math.Round(ValueLockIntervalNumberBox.Value ?? 10d));
        _settings.Replace(_workingCopy);
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs eventArgs)
    {
        DialogResult = false;
    }

    private sealed record SettingsNavigationItem(
        string Title,
        string Symbol,
        bool IsEnabled);

    private sealed record ShortcutDisplayItem(
        string Name,
        string Gesture,
        string Description);

    private sealed record OptionItem(int Value, string Label);

    private sealed record StringOptionItem(string Value, string Label);
}
