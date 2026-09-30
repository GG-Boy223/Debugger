using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Xml.Linq;
using AvalonDock;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using DogeDebugger.Debugger.Plugins;
using DogeDebugger.Core.Settings;
using DogeDebugger.Core.Trace;
using DogeDebugger.PluginSdk;
using DogeDebugger.UI.ViewModels;
using DogeDebugger.UI.Views.Dialogs;
using Microsoft.Win32;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace DogeDebugger;

public partial class MainWindow : FluentWindow
{
    private static readonly string? DiagnosticLogPath =
        Environment.GetEnvironmentVariable("DOGEDEBUGGER_DIAGNOSTIC_LOG");

    private const int CurrentLayoutVersion = 5_000_002;

    private static readonly HashSet<string> NonPersistedPanelIds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "LogPanel",
            "BreakpointPanel",
            "CommentPanel",
            "CustomSymbolPanel",
            "NotesPanel",
            "SourceDebugPanel",
            "StaticAnalysisPanel",
            "McpDebugPanel",
            "McpToolDebugPanel",
            "StringRefPanel",
            "TraceResultPanel",
            "UnrealExplorerPanel"
        };

    private readonly Dictionary<string, HiddenDocumentState> _hiddenDocuments =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PluginPanelRegistration> _pluginPanels =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PluginHotkeyRegistration> _pluginHotkeys =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PluginHotkeyBinding> _pluginHotkeyBindings =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MenuItem> _pluginTopMenus =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<MenuItem>> _pluginTopMenuItems =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<MenuItem>> _pluginPanelMenuItems =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LayoutDocument> _knownPanelDocuments =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _materializedPanelIds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DetachedPanelDocument> _detachedPanelDocuments = [];
    private readonly Dictionary<PanelGroup, string> _lastSelectedPanelIds = [];
    private PanelGroup _activePanelGroup = PanelGroup.Debugging;
    private bool _switchingPanelGroup;
    private bool _needsLayoutReset;
    private PluginRuntime? _pluginRuntime;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnMainDataContextChanged;
        CheatSheetGroups.ItemsSource = CreateCheatSheetGroups();
        RegisterDebuggerCommands();
        RegisterMemorySearchCommands();
        RegisterLuaCommands();
        RegisterHelpCommands();
        RegisterToolsCommands();
        RegisterTraceCommands();
        RegisterViewCommands();
        RegisterFileCommands();
        RegisterSettingsCommands();
        RegisterPluginCommands();
        InitializePanelGroups();
    }

    private void OnOpenAiAssistantClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        AiAssistantWindow window = new(viewModel.Settings.Current)
        {
            Owner = this
        };
        window.Show();
    }

    private void OnMainDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        DisassemblyViewControl.DataContext = eventArgs.NewValue;
        HexViewControl.DataContext = eventArgs.NewValue;
        RegisterViewControl.DataContext = eventArgs.NewValue;
        CallStackViewControl.DataContext = eventArgs.NewValue;
    }

    public void AttachPluginRuntime(PluginRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (ReferenceEquals(_pluginRuntime, runtime))
        {
            return;
        }

        DetachPluginRuntime();
        _pluginRuntime = runtime;
        runtime.PanelRegistered += OnPluginPanelRegistered;
        runtime.PanelUnregistered += OnPluginPanelUnregistered;
        runtime.MenuRegistered += OnPluginMenuRegistered;
        runtime.MenuUnregistered += OnPluginMenuUnregistered;
        runtime.HotkeyRegistered += OnPluginHotkeyRegistered;
        runtime.HotkeyUnregistered += OnPluginHotkeyUnregistered;
        runtime.PanelOpenRequested += OnPluginPanelOpenRequested;
        runtime.PluginUnloaded += OnPluginUnloaded;
        runtime.DisassemblyNavigationRequested += OnPluginDisassemblyNavigationRequested;
        runtime.HexNavigationRequested += OnPluginHexNavigationRequested;
    }

    private void DetachPluginRuntime()
    {
        if (_pluginRuntime is null)
        {
            return;
        }

        _pluginRuntime.PanelRegistered -= OnPluginPanelRegistered;
        _pluginRuntime.PanelUnregistered -= OnPluginPanelUnregistered;
        _pluginRuntime.MenuRegistered -= OnPluginMenuRegistered;
        _pluginRuntime.MenuUnregistered -= OnPluginMenuUnregistered;
        _pluginRuntime.HotkeyRegistered -= OnPluginHotkeyRegistered;
        _pluginRuntime.HotkeyUnregistered -= OnPluginHotkeyUnregistered;
        _pluginRuntime.PanelOpenRequested -= OnPluginPanelOpenRequested;
        _pluginRuntime.PluginUnloaded -= OnPluginUnloaded;
        _pluginRuntime.DisassemblyNavigationRequested -= OnPluginDisassemblyNavigationRequested;
        _pluginRuntime.HexNavigationRequested -= OnPluginHexNavigationRequested;
        _pluginRuntime = null;
    }

    private void OnPluginPanelRegistered(PluginPanelRegistration registration)
    {
        _pluginPanels[registration.ContentId] = registration;
        MenuItem menuItem = new()
        {
            Header = registration.Title
        };
        menuItem.Click += (_, _) => ShowPluginPanel(registration.ContentId);
        PluginMenu.Items.Add(menuItem);
        if (!_pluginPanelMenuItems.TryGetValue(
                registration.PluginId,
                out List<MenuItem>? panelMenuItems))
        {
            panelMenuItems = [];
            _pluginPanelMenuItems.Add(registration.PluginId, panelMenuItems);
        }

        panelMenuItems.Add(menuItem);
    }

    private void OnPluginMenuRegistered(PluginMenuRegistration registration)
    {
        if (!registration.IsMainMenu)
        {
            AttachPluginContextMenu(registration);
            return;
        }

        if (!_pluginTopMenus.TryGetValue(registration.Header, out MenuItem? menu))
        {
            menu = new MenuItem
            {
                Header = registration.Header
            };
            _pluginTopMenus.Add(registration.Header, menu);
            int pluginMenuIndex = MainMenu.Items.IndexOf(PluginMenu);
            if (pluginMenuIndex >= 0)
            {
                MainMenu.Items.Insert(pluginMenuIndex, menu);
            }
            else
            {
                MainMenu.Items.Add(menu);
            }
        }

        List<MenuItem> addedItems = [];
        foreach (PluginMenuItem item in registration.Items)
        {
            object menuElement = CreatePluginMenuItem(registration.PluginId, item);
            if (menuElement is MenuItem menuItem)
            {
                menu.Items.Add(menuItem);
                addedItems.Add(menuItem);
            }
            else if (menuElement is Separator separator)
            {
                menu.Items.Add(separator);
            }
        }

        if (!_pluginTopMenuItems.TryGetValue(
                registration.PluginId,
                out List<MenuItem>? topMenuItems))
        {
            topMenuItems = [];
            _pluginTopMenuItems.Add(registration.PluginId, topMenuItems);
        }

        topMenuItems.AddRange(addedItems);
    }

    private void AttachPluginContextMenu(PluginMenuRegistration registration)
    {
        FrameworkElement? target = registration.ContextTarget switch
        {
            PluginContextMenuTarget.Disassembly => DisassemblyDocument.Content as FrameworkElement,
            PluginContextMenuTarget.Hex => DisassemblyDocument.Content as FrameworkElement,
            PluginContextMenuTarget.AddressList => MemorySearchDocument.Content as FrameworkElement,
            _ => null
        };
        if (target is null)
        {
            return;
        }

        ContextMenu contextMenu = target.ContextMenu ?? new ContextMenu();
        MenuItem menu = new()
        {
            Header = registration.Header
        };
        foreach (PluginMenuItem item in registration.Items)
        {
            menu.Items.Add(CreatePluginMenuItem(registration.PluginId, item));
        }

        contextMenu.Items.Add(menu);
        target.ContextMenu = contextMenu;
    }

    private object CreatePluginMenuItem(string pluginId, PluginMenuItem item)
    {
        if (item.IsSeparator)
        {
            return new Separator();
        }

        MenuItem menuItem = new()
        {
            Header = item.Header
        };
        if (!string.IsNullOrWhiteSpace(item.HotkeyId) &&
            _pluginHotkeys.TryGetValue(
                PluginHotkeyRegistration.CreateKey(pluginId, item.HotkeyId),
                out PluginHotkeyRegistration? hotkey))
        {
            menuItem.InputGestureText = FormatPluginGesture(hotkey.Gesture);
        }

        if (item.Handler is not null)
        {
            menuItem.Click += async (_, _) =>
            {
                try
                {
                    await item.Handler(CancellationToken.None);
                }
                catch (Exception exception)
                {
                    System.Windows.MessageBox.Show(
                        this,
                        exception.Message,
                        "插件命令失败",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Error);
                }
            };
        }

        foreach (PluginMenuItem child in item.Items)
        {
            menuItem.Items.Add(CreatePluginMenuItem(pluginId, child));
        }

        return menuItem;
    }

    private void OnPluginHotkeyRegistered(PluginHotkeyRegistration registration)
    {
        if (!Enum.TryParse(registration.Gesture.Key, ignoreCase: true, out Key key))
        {
            System.Windows.MessageBox.Show(
                this,
                $"插件 {registration.PluginId} 注册了无效快捷键：{registration.Gesture.Key}",
                "插件快捷键",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        RoutedCommand command = new();
        CommandBinding commandBinding = new(
            command,
            async (_, eventArgs) =>
            {
                try
                {
                    await registration.Handler(CancellationToken.None);
                }
                catch (Exception exception)
                {
                    System.Windows.MessageBox.Show(
                        this,
                        exception.Message,
                        "插件快捷键失败",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Error);
                }

                eventArgs.Handled = true;
            });
        KeyBinding keyBinding = new(command, key, ToModifierKeys(registration.Gesture.Modifiers));
        CommandBindings.Add(commandBinding);
        InputBindings.Add(keyBinding);
        _pluginHotkeys[registration.HotkeyKey] = registration;
        _pluginHotkeyBindings[registration.HotkeyKey] = new PluginHotkeyBinding(
            commandBinding,
            keyBinding);
    }

    private void OnPluginHotkeyUnregistered(string pluginId, string hotkeyId)
    {
        string key = PluginHotkeyRegistration.CreateKey(pluginId, hotkeyId);
        if (!_pluginHotkeyBindings.Remove(key, out PluginHotkeyBinding? binding))
        {
            return;
        }

        CommandBindings.Remove(binding.CommandBinding);
        InputBindings.Remove(binding.KeyBinding);
        _pluginHotkeys.Remove(key);
    }

    private void OnPluginPanelUnregistered(string pluginId)
    {
        foreach (PluginPanelRegistration registration in _pluginPanels.Values
                     .Where(registration =>
                         string.Equals(
                             registration.PluginId,
                             pluginId,
                             StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            _pluginPanels.Remove(registration.ContentId);
            LayoutDocument? document = FindDocument(registration.ContentId);
            if (document?.Parent is LayoutDocumentPane pane)
            {
                pane.RemoveChild(document);
            }

            _knownPanelDocuments.Remove(registration.ContentId);
        }

        if (_pluginPanelMenuItems.Remove(pluginId, out List<MenuItem>? panelMenuItems))
        {
            foreach (MenuItem menuItem in panelMenuItems)
            {
                PluginMenu.Items.Remove(menuItem);
            }
        }
    }

    private void OnPluginMenuUnregistered(string pluginId)
    {
        if (!_pluginTopMenuItems.Remove(pluginId, out List<MenuItem>? menuItems))
        {
            return;
        }

        foreach (MenuItem menuItem in menuItems)
        {
            if (menuItem.Parent is ItemsControl parentMenu)
            {
                parentMenu.Items.Remove(menuItem);
            }
        }

        foreach ((string header, MenuItem menu) in _pluginTopMenus.ToArray())
        {
            if (menu.Items.Count == 0)
            {
                MainMenu.Items.Remove(menu);
                _pluginTopMenus.Remove(header);
            }
        }
    }

    private void OnPluginUnloaded(string pluginId)
    {
        OnPluginPanelUnregistered(pluginId);
        OnPluginMenuUnregistered(pluginId);
        _pluginTopMenuItems.Remove(pluginId);
        _pluginPanelMenuItems.Remove(pluginId);
    }

    private void OnPluginPanelOpenRequested(string pluginId, string panelId)
    {
        ShowPluginPanel(PluginPanelRegistration.CreateContentId(pluginId, panelId));
    }

    private void ShowPluginPanel(string contentId)
    {
        LayoutDocument? document = FindDocument(contentId);
        if (document is null &&
            _pluginPanels.TryGetValue(contentId, out PluginPanelRegistration? registration))
        {
            LayoutDocumentPane? pane = GetActiveDocumentPane();
            if (pane is null)
            {
                return;
            }

            document = new LayoutDocument
            {
                ContentId = contentId,
                Title = registration.Title,
                CanClose = true,
                Content = registration.Factory()
            };
            pane.Children.Add(document);
            _knownPanelDocuments[contentId] = document;
        }

        if (document is not null)
        {
            ShowDocument(document);
        }
    }

    private async void OnPluginDisassemblyNavigationRequested(string pluginId, ulong address)
    {
        if (DataContext is MainViewModel viewModel)
        {
            await viewModel.NavigateToAddressAsync(address);
        }

        ShowDocument(DisassemblyDocument);
    }

    private async void OnPluginHexNavigationRequested(string pluginId, ulong address)
    {
        if (DataContext is MainViewModel viewModel)
        {
            await viewModel.NavigateToAddressAsync(address);
        }

        ShowDocument(DisassemblyDocument);
    }

    private static ModifierKeys ToModifierKeys(PluginKeyModifiers modifiers)
    {
        ModifierKeys result = ModifierKeys.None;
        if ((modifiers & PluginKeyModifiers.Alt) != 0)
        {
            result |= ModifierKeys.Alt;
        }

        if ((modifiers & PluginKeyModifiers.Control) != 0)
        {
            result |= ModifierKeys.Control;
        }

        if ((modifiers & PluginKeyModifiers.Shift) != 0)
        {
            result |= ModifierKeys.Shift;
        }

        if ((modifiers & PluginKeyModifiers.Windows) != 0)
        {
            result |= ModifierKeys.Windows;
        }

        return result;
    }

    private static string FormatPluginGesture(PluginKeyGesture gesture)
    {
        List<string> parts = [];
        if ((gesture.Modifiers & PluginKeyModifiers.Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((gesture.Modifiers & PluginKeyModifiers.Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((gesture.Modifiers & PluginKeyModifiers.Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((gesture.Modifiers & PluginKeyModifiers.Windows) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(gesture.Key);
        return string.Join("+", parts);
    }

    protected override void OnClosing(CancelEventArgs eventArgs)
    {
        if (!eventArgs.Cancel && DataContext is MainViewModel viewModel)
        {
            viewModel.Settings.Current.GlobalNotes = viewModel.NotesText;
            viewModel.Settings.Current.SourceDebugging.Enabled =
                viewModel.IsSourceDebuggingEnabled;
            viewModel.Settings.Current.SourceDebugging.SourceRoot =
                viewModel.SourceRoot;
            viewModel.Settings.Current.SourceDebugging.PdbRoot =
                viewModel.PdbRoot;
            SaveWindowLayout(viewModel.Settings);
        }

        if (!eventArgs.Cancel)
        {
            DetachPluginRuntime();
        }

        base.OnClosing(eventArgs);
    }

    protected override void OnContentRendered(EventArgs eventArgs)
    {
        base.OnContentRendered(eventArgs);
        if (_needsLayoutReset)
        {
            _needsLayoutReset = false;
            ResetLayout();
        }

        string? path = Environment.GetEnvironmentVariable(
            "DOGEDEBUGGER_LAYOUT_DUMP");
        if (!string.IsNullOrWhiteSpace(path))
        {
            StringBuilder builder = new();
            AppendVisualTree(builder, this, 0);
            File.WriteAllText(
                path,
                builder.ToString(),
                new UTF8Encoding(false));
        }
    }

    private static void AppendVisualTree(
        StringBuilder builder,
        DependencyObject node,
        int depth)
    {
        builder.Append(' ', depth * 2);
        builder.Append(node.GetType().FullName);
        if (node is FrameworkElement element)
        {
            builder.Append(" name=");
            builder.Append(element.Name);
            builder.Append(" actual=");
            builder.Append(element.ActualWidth.ToString("0.###"));
            builder.Append('x');
            builder.Append(element.ActualHeight.ToString("0.###"));
            builder.Append(" data=");
            builder.Append(element.DataContext?.GetType().FullName ?? "null");
            builder.Append(" visibility=");
            builder.Append(element.Visibility);
        }

        builder.AppendLine();
        int childCount = VisualTreeHelper.GetChildrenCount(node);
        for (int index = 0; index < childCount; index++)
        {
            AppendVisualTree(
                builder,
                VisualTreeHelper.GetChild(node, index),
                depth + 1);
        }
    }

    internal void RestoreWindowLayout(SettingsStore settings)
    {
        if (!settings.Current.SaveWindowLayout)
        {
            return;
        }

        try
        {
            WindowLayoutData? windowLayout = settings.Current.WindowLayout;
            if (windowLayout is not null)
            {
                Left = windowLayout.Left;
                Top = windowLayout.Top;
                Width = windowLayout.Width;
                Height = windowLayout.Height;
                if (windowLayout.IsMaximized)
                {
                    WindowState = WindowState.Maximized;
                }

                WindowStartupLocation = WindowStartupLocation.Manual;
            }

            ApplyActivityBarPosition(settings.Current.ActivityBarPosition == 1);

            bool restoreDockLayout =
                settings.Current.LayoutVersion >= CurrentLayoutVersion;
            if (!restoreDockLayout)
            {
                settings.Current.MainDockLayout = null;
                settings.Current.DisasmDockLayout = null;
            }
            else if (!string.IsNullOrWhiteSpace(settings.Current.MainDockLayout))
            {
                Dictionary<string, object> panelContents = _knownPanelDocuments
                    .Where(static pair => pair.Value.Content is not null)
                    .ToDictionary(
                        static pair => pair.Key,
                        static pair => pair.Value.Content!,
                        StringComparer.OrdinalIgnoreCase);
                _knownPanelDocuments.Clear();
                RestoreDockLayout(DockManager, settings.Current.MainDockLayout);
                RestorePanelContents(DockManager, panelContents);
                RefreshDocumentReferences();
                EnsureOptionalDocuments();
            }

            if (restoreDockLayout &&
                !string.IsNullOrWhiteSpace(settings.Current.DisasmDockLayout))
            {
                RestoreDisassemblyDockLayout(settings.Current.DisasmDockLayout);
            }

            CaptureKnownPanelDocuments();
            PanelGroup restoredGroup = Enum.IsDefined(
                typeof(PanelGroup),
                settings.Current.LastActiveGroup)
                ? (PanelGroup)settings.Current.LastActiveGroup
                : PanelGroup.Debugging;
            SwitchPanelGroup(restoredGroup);
            ShowStartupPanelFromEnvironment();
            LayoutDocumentPane? activePane = GetActiveDocumentPane();
            if (activePane is null ||
                !activePane.Children
                    .OfType<LayoutDocument>()
                    .Any(static document => document.Content is not null))
            {
                _needsLayoutReset = true;
            }

            settings.Current.LayoutVersion = CurrentLayoutVersion;
        }
        catch
        {
        }
    }

    private void SaveWindowLayout(SettingsStore settings)
    {
        if (!settings.Current.SaveWindowLayout)
        {
            return;
        }

        try
        {
            settings.Current.WindowLayout = new WindowLayoutData
            {
                Left = Left,
                Top = Top,
                Width = Width,
                Height = Height,
                IsMaximized = WindowState == WindowState.Maximized
            };
            settings.Current.LastActiveGroup = (int)_activePanelGroup;
            settings.Current.ActivityBarPosition = ActivityBarLeftMenuItem.IsChecked
                ? 0
                : 1;
            RestoreHiddenDocuments();
            ReattachAllPanelDocuments();
            settings.Current.MainDockLayout = SerializeDockLayout(DockManager);
            settings.Current.DisasmDockLayout = SerializeDockLayout(DisasmDockManager);
            settings.Save();
            SwitchPanelGroup(_activePanelGroup);
        }
        catch
        {
        }
    }

    private static string SerializeDockLayout(DockingManager manager)
    {
        XmlLayoutSerializer serializer = new(manager);
        using StringWriter writer = new();
        serializer.Serialize(writer);

        XDocument document = XDocument.Parse(writer.ToString());
        document.Descendants()
            .Where(static element =>
            {
                string? contentId = (string?)element.Attribute("ContentId");
                return contentId is not null &&
                       (NonPersistedPanelIds.Contains(contentId) ||
                        contentId.StartsWith("Dynamic.", StringComparison.Ordinal));
            })
            .Remove();

        XDeclaration? declaration = document.Declaration;
        return declaration is null
            ? document.ToString()
            : declaration + Environment.NewLine + document;
    }

    private static void RestoreDockLayout(DockingManager manager, string layout)
    {
        XmlLayoutSerializer serializer = new(manager);
        using StringReader reader = new(layout);
        serializer.Deserialize(reader);
    }

    private static void RestorePanelContents(
        DockingManager manager,
        IReadOnlyDictionary<string, object> panelContents)
    {
        foreach (LayoutDocument document in manager.Layout
                     .Descendents()
                     .OfType<LayoutDocument>())
        {
            if (document.Content is not null ||
                string.IsNullOrWhiteSpace(document.ContentId))
            {
                continue;
            }

            if (panelContents.TryGetValue(
                    document.ContentId,
                    out object? content))
            {
                document.Content = content;
            }
        }
    }

    private void RestoreDisassemblyDockLayout(string layout)
    {
        Dictionary<string, object> anchorableContents = DisasmDockManager.Layout
            .Descendents()
            .OfType<LayoutAnchorable>()
            .Where(static anchorable =>
                !string.IsNullOrWhiteSpace(anchorable.ContentId) &&
                anchorable.Content is not null)
            .ToDictionary(
                static anchorable => anchorable.ContentId,
                static anchorable => anchorable.Content!,
                StringComparer.OrdinalIgnoreCase);

        RestoreDockLayout(DisasmDockManager, layout);
        foreach (LayoutAnchorable anchorable in DisasmDockManager.Layout
                     .Descendents()
                     .OfType<LayoutAnchorable>())
        {
            string? contentId = GetDisassemblyAnchorableId(anchorable);
            if (anchorable.Content is null &&
                !string.IsNullOrWhiteSpace(contentId) &&
                anchorableContents.TryGetValue(
                    contentId,
                    out object? content))
            {
                anchorable.Content = content;
                anchorable.ContentId = contentId;
            }
        }

        EnsureDisassemblyAnchorablesVisible();
        DisasmDockManager.UpdateLayout();
    }

    private void EnsureDisassemblyAnchorablesVisible()
    {
        LayoutAnchorablePane[] panes = DisasmDockManager.Layout
            .Descendents()
            .OfType<LayoutAnchorablePane>()
            .ToArray();
        if (panes.Length == 0)
        {
            return;
        }

        string[] contentIds =
        [
            "DisassemblyView",
            "HexView",
            "RegisterView",
            "CallStackView"
        ];
        for (int index = 0; index < contentIds.Length; index++)
        {
            LayoutAnchorable? anchorable = DisasmDockManager.Layout
                .Descendents()
                .OfType<LayoutAnchorable>()
                .FirstOrDefault(candidate => string.Equals(
                    GetDisassemblyAnchorableId(candidate),
                    contentIds[index],
                    StringComparison.OrdinalIgnoreCase));
            if (anchorable is null ||
                anchorable.Parent is LayoutAnchorablePane)
            {
                continue;
            }

            DisasmDockManager.Layout.Hidden.Remove(anchorable);
            LayoutAnchorablePane pane = panes[Math.Min(index, panes.Length - 1)];
            if (!pane.Children.Contains(anchorable))
            {
                pane.Children.Add(anchorable);
            }

            anchorable.IsSelected = true;
        }
    }

    private static string? GetDisassemblyAnchorableId(
        LayoutAnchorable anchorable)
    {
        if (!string.IsNullOrWhiteSpace(anchorable.ContentId))
        {
            return anchorable.ContentId;
        }

        return anchorable.Title switch
        {
            "反汇编" => "DisassemblyView",
            "数据视图" => "HexView",
            "寄存器" => "RegisterView",
            "调用堆栈" => "CallStackView",
            _ => null
        };
    }

    private void OnActivityBarLeftClick(object sender, RoutedEventArgs eventArgs)
    {
        ApplyActivityBarPosition(positionRight: false);
        eventArgs.Handled = true;
    }

    private void ShowStartupPanelFromEnvironment()
    {
        string? contentId = Environment.GetEnvironmentVariable(
            "DOGEDEBUGGER_STARTUP_PANEL");
        if (string.IsNullOrWhiteSpace(contentId))
        {
            return;
        }

        LayoutDocument? document = FindDocument(contentId);
        if (document is not null)
        {
            _materializedPanelIds.Add(contentId);
            ShowDocument(document);
        }
    }

    private void OnActivityBarRightClick(object sender, RoutedEventArgs eventArgs)
    {
        ApplyActivityBarPosition(positionRight: true);
        eventArgs.Handled = true;
    }

    private void ApplyActivityBarPosition(bool positionRight)
    {
        Grid.SetColumn(ActivityBar, positionRight ? 1 : 0);
        Grid.SetColumn(DockManager, positionRight ? 0 : 1);
        ActivityBarColumn.Width = positionRight
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(40);
        DockColumn.Width = positionRight
            ? new GridLength(40)
            : new GridLength(1, GridUnitType.Star);
        ActivityBar.BorderThickness = positionRight
            ? new Thickness(1, 0, 0, 0)
            : new Thickness(0, 0, 1, 0);
        ActivityBarLeftMenuItem.IsChecked = !positionRight;
        ActivityBarRightMenuItem.IsChecked = positionRight;
    }

    private void InitializePanelGroups()
    {
        CaptureKnownPanelDocuments();
        SwitchPanelGroup(_activePanelGroup);
    }

    private void CaptureKnownPanelDocuments()
    {
        foreach (LayoutDocument document in DockManager.Layout
                     .Descendents()
                     .OfType<LayoutDocument>())
        {
            string contentId = document.ContentId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(contentId))
            {
                PanelDescriptor? descriptor = PanelCatalog.Panels.FirstOrDefault(
                    panel => string.Equals(
                        panel.Title,
                        document.Title,
                        StringComparison.OrdinalIgnoreCase));
                if (descriptor is not null)
                {
                    contentId = descriptor.ContentId;
                    document.ContentId = contentId;
                }
            }

            if (!string.IsNullOrWhiteSpace(contentId))
            {
                _knownPanelDocuments[contentId] = document;
            }
        }

        ApplyPanelIcons();
    }

    private void ApplyPanelIcons()
    {
        foreach (PanelDescriptor descriptor in PanelCatalog.Panels)
        {
            if (descriptor.Icon is not { } icon ||
                !_knownPanelDocuments.TryGetValue(
                    descriptor.ContentId,
                    out LayoutDocument? document) ||
                document.IconSource is not null)
            {
                continue;
            }

            document.IconSource = CreateSymbolImage(icon);
        }
    }

    private static ImageSource CreateSymbolImage(SymbolRegular symbol)
    {
        FontFamily family =
            Application.Current.TryFindResource("FluentSystemIcons") as FontFamily ??
            new FontFamily(
                new Uri("pack://application:,,,/Wpf.Ui;component/"),
                "./Resources/Fonts/#FluentSystemIcons-Regular");
        Typeface typeface = new(
            family,
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal);
        FormattedText text = new(
            char.ConvertFromUtf32((int)symbol),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            16,
            new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A)),
            1);
        DrawingVisual visual = new();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawText(text, new Point(1, 0));
        }

        RenderTargetBitmap bitmap = new(
            20,
            20,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private void OnPanelGroupClick(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not FrameworkElement { Tag: PanelGroup group })
        {
            return;
        }

        SwitchPanelGroup(group);
        eventArgs.Handled = true;
    }

    private void SwitchPanelGroup(
        PanelGroup group,
        LayoutDocument? requestedDocument = null)
    {
        if (_switchingPanelGroup)
        {
            return;
        }

        if (!Enum.IsDefined(group))
        {
            group = PanelGroup.Debugging;
        }

        _switchingPanelGroup = true;
        try
        {
            ReattachAllPanelDocuments();
            CaptureKnownPanelDocuments();

            LayoutDocumentPane? pane = GetActiveDocumentPane();
            if (pane is null)
            {
                return;
            }

            LayoutDocument? selectedDocument =
                pane.SelectedContent as LayoutDocument ??
                pane.Children.OfType<LayoutDocument>().FirstOrDefault(
                    static document => document.IsActive);
            if (selectedDocument?.ContentId is { Length: > 0 } selectedContentId)
            {
                _lastSelectedPanelIds[_activePanelGroup] = selectedContentId;
            }

            foreach (LayoutDocument document in pane.Children
                         .OfType<LayoutDocument>()
                         .ToArray())
            {
                _detachedPanelDocuments.Add(new DetachedPanelDocument(
                    document,
                    pane,
                    pane.Children.IndexOf(document)));
                pane.RemoveChild(document);
            }

            _activePanelGroup = group;

            foreach (PanelDescriptor descriptor in PanelCatalog.ForGroup(group))
            {
                if (descriptor.CreatedOnDemand &&
                    !_materializedPanelIds.Contains(descriptor.ContentId))
                {
                    continue;
                }

                if (_hiddenDocuments.ContainsKey(descriptor.ContentId))
                {
                    continue;
                }

                if (_knownPanelDocuments.TryGetValue(
                        descriptor.ContentId,
                        out LayoutDocument? document) &&
                    document.Parent is null)
                {
                    pane.Children.Add(document);
                }
            }

            foreach (LayoutDocument document in _knownPanelDocuments.Values
                         .Where(document =>
                             ResolveGroup(document) == PanelGroup.Plugins &&
                             !_hiddenDocuments.ContainsKey(
                                 document.ContentId ?? string.Empty))
                         .OrderBy(static document => document.Title))
            {
                if (document.Parent is null)
                {
                    pane.Children.Add(document);
                }
            }

            LayoutDocument? target = requestedDocument;
            if (target is null &&
                _lastSelectedPanelIds.TryGetValue(
                    group,
                    out string? lastSelectedContentId))
            {
                _knownPanelDocuments.TryGetValue(
                    lastSelectedContentId,
                    out target);
            }

            target ??= pane.Children.OfType<LayoutDocument>().FirstOrDefault();
            if (target is not null && target.Parent == pane)
            {
                target.IsSelected = true;
                target.IsActive = true;
            }

            UpdatePanelGroupIndicators();
            WriteDiagnostic(
                $"SwitchPanelGroup {group}: paneChildren={pane.Children.Count}, " +
                $"known={_knownPanelDocuments.Count}, " +
                $"hidden={_hiddenDocuments.Count}, " +
                $"detached={_detachedPanelDocuments.Count}, " +
                $"materialized={_materializedPanelIds.Count}");
        }
        finally
        {
            _switchingPanelGroup = false;
        }
    }

    private void ReattachAllPanelDocuments()
    {
        if (_detachedPanelDocuments.Count == 0)
        {
            return;
        }

        foreach (DetachedPanelDocument detached in _detachedPanelDocuments
                     .OrderBy(static item => item.Index))
        {
            if (detached.Document.Parent is not null)
            {
                continue;
            }

            int index = Math.Clamp(
                detached.Index,
                0,
                detached.Pane.Children.Count);
            detached.Pane.InsertChildAt(index, detached.Document);
        }

        _detachedPanelDocuments.Clear();
    }

    private PanelGroup ResolveGroup(LayoutDocument document)
    {
        if (PanelCatalog.TryGet(document.ContentId, out PanelDescriptor descriptor))
        {
            return descriptor.Group;
        }

        return document.ContentId?.StartsWith(
            "Dynamic.",
            StringComparison.OrdinalIgnoreCase) == true
            ? PanelGroup.Plugins
            : PanelGroup.Debugging;
    }

    private void UpdatePanelGroupIndicators()
    {
        SetPanelGroupIndicator(
            DebuggingGroupIndicator,
            _activePanelGroup == PanelGroup.Debugging);
        SetPanelGroupIndicator(
            SearchAndAnalysisGroupIndicator,
            _activePanelGroup == PanelGroup.SearchAndAnalysis);
        SetPanelGroupIndicator(
            ScriptingGroupIndicator,
            _activePanelGroup == PanelGroup.Scripting);
        SetPanelGroupIndicator(
            InformationGroupIndicator,
            _activePanelGroup == PanelGroup.Information);
        SetPanelGroupIndicator(
            ExtensionsGroupIndicator,
            _activePanelGroup == PanelGroup.Extensions);
        SetPanelGroupIndicator(
            PluginsGroupIndicator,
            _activePanelGroup == PanelGroup.Plugins);
    }

    private static void SetPanelGroupIndicator(Border indicator, bool active)
    {
        indicator.Background = active
            ? new SolidColorBrush(Color.FromRgb(0x00, 0x67, 0xC0))
            : Brushes.Transparent;
    }

    private void RefreshDocumentReferences()
    {
        CaptureKnownPanelDocuments();
        MemorySearchDocument = RegisterPanelDocument(
            "MemorySearchPanel",
            FindDocument("MemorySearchPanel") ?? MemorySearchDocument);
        DisassemblyDocument = RegisterPanelDocument(
            "DisassemblyPanel",
            FindDocument("DisassemblyPanel") ?? DisassemblyDocument);
        SourceDebugDocument = RegisterPanelDocument(
            "SourceDebugPanel",
            FindDocument("SourceDebugPanel") ?? SourceDebugDocument);
        NotesDocument = RegisterPanelDocument(
            "NotesPanel",
            FindDocument("NotesPanel") ?? NotesDocument);
        BreakpointsDocument = RegisterPanelDocument(
            "BreakpointPanel",
            FindDocument("BreakpointPanel") ?? BreakpointsDocument);
        CrossReferencesDocument = RegisterPanelDocument(
            "CrossRefPanel",
            FindDocument("CrossRefPanel") ?? CrossReferencesDocument);
        MemoryMapDocument = RegisterPanelDocument(
            "MemoryMapPanel",
            FindDocument("MemoryMapPanel") ?? MemoryMapDocument);
        StringReferencesDocument = RegisterPanelDocument(
            "StringRefPanel",
            FindDocument("StringRefPanel") ?? StringReferencesDocument);
        RttiDocument = RegisterPanelDocument(
            "RttiPanel",
            FindDocument("RttiPanel") ?? RttiDocument);
        MonoExplorerDocument = RegisterPanelDocument(
            "MonoExplorerPanel",
            FindDocument("MonoExplorerPanel") ?? MonoExplorerDocument);
        UnrealExplorerDocument = RegisterPanelDocument(
            "UnrealExplorerPanel",
            FindDocument("UnrealExplorerPanel") ?? UnrealExplorerDocument);
        ThreadsDocument = RegisterPanelDocument(
            "ThreadListPanel",
            FindDocument("ThreadListPanel") ?? ThreadsDocument);
        HandlesDocument = RegisterPanelDocument(
            "HandleListPanel",
            FindDocument("HandleListPanel") ?? HandlesDocument);
        ExceptionsDocument = RegisterPanelDocument(
            "ExceptionHandlerPanel",
            FindDocument("ExceptionHandlerPanel") ?? ExceptionsDocument);
        ModulesDocument = RegisterPanelDocument(
            "ModuleListPanel",
            FindDocument("ModuleListPanel") ?? ModulesDocument);
        CommentsDocument = RegisterPanelDocument(
            "CommentPanel",
            FindDocument("CommentPanel") ?? CommentsDocument);
        CustomSymbolsDocument = RegisterPanelDocument(
            "CustomSymbolPanel",
            FindDocument("CustomSymbolPanel") ?? CustomSymbolsDocument);
        LogDocument = RegisterPanelDocument(
            "LogPanel",
            FindDocument("LogPanel") ?? LogDocument);
        TraceResultDocument = RegisterPanelDocument(
            "TraceResultPanel",
            FindDocument("TraceResultPanel") ?? TraceResultDocument);
        AutoAssemblerDocument = RegisterPanelDocument(
            "AutoAssemblerPanel",
            FindDocument("AutoAssemblerPanel") ?? AutoAssemblerDocument);
        LuaDocument = RegisterPanelDocument(
            "LuaScriptPanel",
            FindDocument("LuaScriptPanel") ?? LuaDocument);
    }

    private LayoutDocument RegisterPanelDocument(
        string contentId,
        LayoutDocument document)
    {
        _knownPanelDocuments[contentId] = document;
        _materializedPanelIds.Add(contentId);
        return document;
    }

    private LayoutDocument? FindDocument(string contentId)
    {
        if (_knownPanelDocuments.TryGetValue(
                contentId,
                out LayoutDocument? knownDocument))
        {
            return knownDocument;
        }

        LayoutDocument? document = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .FirstOrDefault(document => string.Equals(
                document.ContentId,
                contentId,
                StringComparison.OrdinalIgnoreCase));
        if (document is not null)
        {
            _knownPanelDocuments[contentId] = document;
            return document;
        }

        PanelDescriptor? descriptor = PanelCatalog.Panels.FirstOrDefault(
            panel => string.Equals(
                panel.ContentId,
                contentId,
                StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
        {
            return null;
        }

        document = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocument>()
            .FirstOrDefault(candidate => string.Equals(
                candidate.Title,
                descriptor.Title,
                StringComparison.OrdinalIgnoreCase));
        if (document is not null)
        {
            document.ContentId = descriptor.ContentId;
            _knownPanelDocuments[descriptor.ContentId] = document;
        }

        return document;
    }

    private void EnsureOptionalDocuments()
    {
        LayoutDocumentPane? pane = GetActiveDocumentPane();
        if (pane is null)
        {
            return;
        }

        CommentsDocument = EnsureOptionalDocument(
            "CommentPanel",
            "注释",
            CommentsDocument,
            pane);
        CustomSymbolsDocument = EnsureOptionalDocument(
            "CustomSymbolPanel",
            "自定义符号",
            CustomSymbolsDocument,
            pane);
        NotesDocument = EnsureOptionalDocument(
            "NotesPanel",
            "笔记",
            NotesDocument,
            pane);
        BreakpointsDocument = EnsureOptionalDocument(
            "BreakpointPanel",
            "断点列表",
            BreakpointsDocument,
            pane);
        LogDocument = EnsureOptionalDocument(
            "LogPanel",
            "日志",
            LogDocument,
            pane);
        SourceDebugDocument = EnsureOptionalDocument(
            "SourceDebugPanel",
            "源码调试",
            SourceDebugDocument,
            pane);
        StringReferencesDocument = EnsureOptionalDocument(
            "StringRefPanel",
            "字符串引用",
            StringReferencesDocument,
            pane);
        UnrealExplorerDocument = EnsureOptionalDocument(
            "UnrealExplorerPanel",
            "UnrealEngine 扩展",
            UnrealExplorerDocument,
            pane);
        MonoExplorerDocument = EnsureOptionalDocument(
            "MonoExplorerPanel",
            "Unity 扩展",
            MonoExplorerDocument,
            pane);
        CrossReferencesDocument = EnsureOptionalDocument(
            "CrossRefPanel",
            "交叉引用",
            CrossReferencesDocument,
            pane);
    }

    private LayoutDocument EnsureOptionalDocument(
        string contentId,
        string title,
        LayoutDocument existing,
        LayoutDocumentPane pane)
    {
        LayoutDocument? current = FindDocument(contentId);
        if (current is not null)
        {
            return current;
        }

        object? content = existing.Content;
        existing.Content = null;
        LayoutDocument document = new()
        {
            ContentId = contentId,
            Title = title,
            CanClose = existing.CanClose,
            Content = content
        };
        pane.Children.Add(document);
        return document;
    }

    private void RegisterSettingsCommands()
    {
        CommandBindings.Add(new CommandBinding(
            SettingsCommands.Open,
            (_, eventArgs) =>
            {
                OpenSettings("常规");
                eventArgs.Handled = true;
            }));
        CommandBindings.Add(new CommandBinding(
            SettingsCommands.Shortcuts,
            (_, eventArgs) =>
            {
                OpenSettings("快捷键");
                eventArgs.Handled = true;
            }));
        CommandBindings.Add(new CommandBinding(
            SettingsCommands.ResetLayout,
            (_, eventArgs) =>
            {
                ResetLayout();
                eventArgs.Handled = true;
            }));
    }

    private void RegisterPluginCommands()
    {
        CommandBindings.Add(new CommandBinding(
            PluginCommands.Manage,
            (_, eventArgs) =>
            {
                OpenPluginManager(loadExternal: false);
                eventArgs.Handled = true;
            }));
        CommandBindings.Add(new CommandBinding(
            PluginCommands.Load,
            (_, eventArgs) =>
            {
                OpenPluginManager(loadExternal: true);
                eventArgs.Handled = true;
            }));
    }

    private void OpenPluginManager(bool loadExternal)
    {
        if (_pluginRuntime is null ||
            DataContext is not MainViewModel viewModel)
        {
            return;
        }

        PluginManagerWindow window = new(
            _pluginRuntime,
            viewModel.Settings,
            Path.Combine(AppContext.BaseDirectory, "Plugins"))
        {
            Owner = this
        };
        window.Show();
        if (loadExternal)
        {
            _ = window.LoadExternalPluginAsync();
        }
    }

    private void OpenSettings(string initialPage)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        SettingsWindow window = new(viewModel.Settings, initialPage)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void ResetLayout()
    {
        LayoutDocumentPane? pane = DockManager.Layout
            .Descendents()
            .OfType<LayoutDocumentPane>()
            .FirstOrDefault();
        if (pane is null)
        {
            return;
        }

        LayoutDocument[] documents =
        [
            MemorySearchDocument,
            DisassemblyDocument,
            SourceDebugDocument,
            NotesDocument,
            BreakpointsDocument,
            CrossReferencesDocument,
            MemoryMapDocument,
            StringReferencesDocument,
            RttiDocument,
            UnrealExplorerDocument,
            ThreadsDocument,
            HandlesDocument,
            ExceptionsDocument,
            ModulesDocument,
            CommentsDocument,
            CustomSymbolsDocument,
            LogDocument,
            TraceResultDocument,
            AutoAssemblerDocument,
            LuaDocument
        ];
        foreach (LayoutDocument document in documents)
        {
            if (!pane.Children.Contains(document))
            {
                pane.Children.Add(document);
            }
        }

        _hiddenDocuments.Clear();
        ShowDocument(MemorySearchDocument);
        DockManager.UpdateLayout();
    }

    private void RegisterFileCommands()
    {
        CommandBindings.Add(new CommandBinding(
            FileCommands.OpenProcess,
            (_, eventArgs) =>
            {
                OpenProcessDialog(ProcessSelectionMode.OpenProcess);
                eventArgs.Handled = true;
            }));
        CommandBindings.Add(new CommandBinding(
            FileCommands.Attach,
            (_, args) =>
            {
                OpenProcessDialog(ProcessSelectionMode.Attach);
                args.Handled = true;
            }));
        CommandBindings.Add(new CommandBinding(
            FileCommands.Launch,
            (_, args) =>
            {
                OpenLaunch();
                args.Handled = true;
            }));
        CommandBindings.Add(new CommandBinding(
            FileCommands.Exit,
            (_, eventArgs) =>
            {
                Application.Current.Shutdown();
                eventArgs.Handled = true;
            }));
        InputBindings.Add(new KeyBinding(FileCommands.Attach, Key.A, ModifierKeys.Alt));
        InputBindings.Add(new KeyBinding(FileCommands.Launch, Key.F3, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(FileCommands.Exit, Key.X, ModifierKeys.Alt));
        InputBindings.Add(new KeyBinding(FileCommands.OpenProcess, Key.P, ModifierKeys.Control));
    }

    private async void OpenProcessDialog(ProcessSelectionMode mode)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        ProcessSelectionWindow dialog = new(viewModel, mode)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.SelectedProcess is null)
        {
            return;
        }

        viewModel.SelectedProcess = dialog.SelectedProcess;
        if (mode == ProcessSelectionMode.Attach)
        {
            await viewModel.AttachSelectedCommand.ExecuteAsync(null);
            return;
        }

        if (mode == ProcessSelectionMode.OpenProcess)
        {
            await viewModel.OpenSelectedCommand.ExecuteAsync(null);
        }
    }

    private async void OpenLaunch()
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        OpenFileDialog dialog = new()
        {
            Title = "选择要调试的可执行文件",
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            FilterIndex = 1,
            Multiselect = false,
            CheckFileExists = true,
            CheckPathExists = true,
            DereferenceLinks = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        viewModel.LaunchCommandLine = dialog.FileName;
        await viewModel.LaunchCommand.ExecuteAsync(null);
    }

    private void RegisterViewCommands()
    {
        AddViewCommand(ViewCommands.MemorySearch, MemorySearchDocument);
        AddViewCommand(
            ViewCommands.Disassembly,
            () => ActivateDisassemblySection("DisassemblyView", "反汇编"));
        AddViewCommand(
            ViewCommands.Hex,
            () => ActivateDisassemblySection("HexView", "数据视图"));
        AddViewCommand(
            ViewCommands.Registers,
            () => ActivateDisassemblySection("RegisterView", "寄存器"));
        AddViewCommand(
            ViewCommands.CallStack,
            () => ActivateDisassemblySection("CallStackView", "调用堆栈"));
        AddViewCommand(ViewCommands.TraceResult, TraceResultDocument);
        AddViewCommand(ViewCommands.Modules, ModulesDocument);
        AddViewCommand(ViewCommands.Threads, ThreadsDocument);
        AddViewCommand(ViewCommands.MemoryMap, MemoryMapDocument);
        AddViewCommand(ViewCommands.Handles, HandlesDocument);
        AddViewCommand(ViewCommands.Breakpoints, BreakpointsDocument);
        AddViewCommand(ViewCommands.CrossReferences, CrossReferencesDocument);
        AddViewCommand(
            ViewCommands.SourceDebug,
            () => ShowDocument(
                FindDocument("SourceDebugPanel") ?? SourceDebugDocument));
        AddViewCommand(ViewCommands.StringReferences, StringReferencesDocument);
        AddViewCommand(ViewCommands.Rtti, RttiDocument);
        AddViewCommand(ViewCommands.Unity, MonoExplorerDocument);
        AddViewCommand(ViewCommands.UnrealEngine, UnrealExplorerDocument);
        AddViewCommand(ViewCommands.Exceptions, ExceptionsDocument);
        AddViewCommand(ViewCommands.Notes, NotesDocument);
        AddViewCommand(ViewCommands.Comments, CommentsDocument);
        AddViewCommand(ViewCommands.CustomSymbols, CustomSymbolsDocument);
        AddViewCommand(ViewCommands.Log, LogDocument);
        AddViewCommand(ViewCommands.AutoAssembler, AutoAssemblerDocument);
        AddViewCommand(ViewCommands.LuaScript, LuaDocument);
        CommandBindings.Add(new CommandBinding(
            PanelCommands.Next,
            (_, eventArgs) =>
            {
                NavigatePanel(1);
                eventArgs.Handled = true;
            }));
        CommandBindings.Add(new CommandBinding(
            PanelCommands.Previous,
            (_, eventArgs) =>
            {
                NavigatePanel(-1);
                eventArgs.Handled = true;
            }));
        CommandBindings.Add(new CommandBinding(
            PanelCommands.Close,
            (_, eventArgs) =>
            {
                CloseCurrentPanel();
                eventArgs.Handled = true;
            }));
        InputBindings.Add(new KeyBinding(PanelCommands.Next, Key.Tab, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(
            PanelCommands.Previous,
            Key.Tab,
            ModifierKeys.Control | ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(PanelCommands.Close, Key.W, ModifierKeys.Control));
    }

    private void RegisterMemorySearchCommands()
    {
        CommandBindings.Add(new CommandBinding(
            MemorySearchCommands.FirstScan,
            (_, args) => ExecuteViewModelCommand(
                args,
                static viewModel => viewModel.MemorySearch.FirstScanCommand),
            (_, args) => CanExecuteViewModelCommand(
                args,
                static viewModel => viewModel.MemorySearch.FirstScanCommand)));
        CommandBindings.Add(new CommandBinding(
            MemorySearchCommands.NextScan,
            (_, args) => ExecuteViewModelCommand(
                args,
                static viewModel => viewModel.MemorySearch.NextScanCommand),
            (_, args) => CanExecuteViewModelCommand(
                args,
                static viewModel => viewModel.MemorySearch.NextScanCommand)));
        CommandBindings.Add(new CommandBinding(
            MemorySearchCommands.NewTab,
            (_, args) =>
            {
                ShowDocument(MemorySearchDocument);
                ExecuteViewModelCommand(
                    args,
                    static viewModel => viewModel.MemorySearchWorkspace.NewTabCommand);
            },
            (_, args) => CanExecuteViewModelCommand(
                args,
                static viewModel => viewModel.MemorySearchWorkspace.NewTabCommand)));
        InputBindings.Add(new KeyBinding(
            MemorySearchCommands.FirstScan,
            Key.S,
            ModifierKeys.Control | ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(
            MemorySearchCommands.NextScan,
            Key.N,
            ModifierKeys.Control | ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(
            MemorySearchCommands.NewTab,
            Key.T,
            ModifierKeys.Control));
    }

    private void RegisterLuaCommands()
    {
        CommandBindings.Add(new CommandBinding(
            LuaCommands.Run,
            (_, args) => ExecuteViewModelCommand(
                args,
                static viewModel => viewModel.RunLuaCommand),
            (_, args) => CanExecuteViewModelCommand(
                args,
                static viewModel => viewModel.RunLuaCommand)));
        CommandBindings.Add(new CommandBinding(
            LuaCommands.Stop,
            (_, args) => ExecuteViewModelCommand(
                args,
                static viewModel => viewModel.StopLuaCommand),
            (_, args) => CanExecuteViewModelCommand(
                args,
                static viewModel => viewModel.StopLuaCommand)));
        InputBindings.Add(new KeyBinding(
            LuaCommands.Run,
            Key.F11,
            ModifierKeys.Control | ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(
            LuaCommands.Stop,
            Key.F12,
            ModifierKeys.Control | ModifierKeys.Shift));
    }

    private void RegisterHelpCommands()
    {
        CommandBindings.Add(new CommandBinding(
            HelpCommands.About,
            (_, eventArgs) =>
            {
                AboutWindow window = new()
                {
                    Owner = this
                };
                window.ShowDialog();
                eventArgs.Handled = true;
            }));
        CommandBindings.Add(new CommandBinding(
            HelpCommands.CheatSheet,
            (_, eventArgs) =>
            {
                ToggleShortcutCheatSheet();
                eventArgs.Handled = true;
            }));
        InputBindings.Add(new KeyBinding(
            HelpCommands.CheatSheet,
            Key.OemQuestion,
            ModifierKeys.Control | ModifierKeys.Shift));
    }

    private void RegisterToolsCommands()
    {
        CommandBindings.Add(new CommandBinding(
            ToolsCommands.AllocateMemory,
            (_, eventArgs) =>
            {
                OpenAllocateMemory();
                eventArgs.Handled = true;
            }));
        CommandBindings.Add(new CommandBinding(
            ToolsCommands.CreateThread,
            (_, eventArgs) =>
            {
                OpenCreateThread();
                eventArgs.Handled = true;
            }));
        CommandBindings.Add(new CommandBinding(
            ToolsCommands.CallFunction,
            (_, eventArgs) =>
            {
                OpenCallFunction();
                eventArgs.Handled = true;
            }));
        CommandBindings.Add(new CommandBinding(
            ToolsCommands.DllInject,
            (_, eventArgs) =>
            {
                OpenDllInjection();
                eventArgs.Handled = true;
            }));
        CommandBindings.Add(new CommandBinding(
            ToolsCommands.PointerScan,
            (_, eventArgs) =>
            {
                OpenPointerScan();
                eventArgs.Handled = true;
            }));
        CommandBindings.Add(new CommandBinding(
            ToolsCommands.SignatureSearch,
            (_, eventArgs) =>
            {
                OpenSignatureSearch();
                eventArgs.Handled = true;
            }));
        CommandBindings.Add(new CommandBinding(
            ToolsCommands.CacheManager,
            (_, eventArgs) =>
            {
                CacheManagerWindow window = new()
                {
                    Owner = this
                };
                window.ShowDialog();
                eventArgs.Handled = true;
            }));
    }

    private void RegisterTraceCommands()
    {
        CommandBindings.Add(new CommandBinding(
            TraceCommands.Start,
            (_, eventArgs) =>
            {
                OpenTraceConfiguration();
                eventArgs.Handled = true;
            }));
        CommandBindings.Add(new CommandBinding(
            TraceCommands.Stop,
            async (_, eventArgs) =>
            {
                if (DataContext is MainViewModel viewModel)
                {
                    await viewModel.StopTraceAsync();
                }

                eventArgs.Handled = true;
            }));
        CommandBindings.Add(new CommandBinding(
            TraceCommands.ShowResult,
            (_, eventArgs) =>
            {
                ShowDocument(TraceResultDocument);
                eventArgs.Handled = true;
            }));
        InputBindings.Add(new KeyBinding(
            TraceCommands.Start,
            Key.F7,
            ModifierKeys.Control | ModifierKeys.Alt));
    }

    private async void OpenAllocateMemory()
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (!viewModel.IsTargetOpen)
        {
            await ShowInformationAsync("请先打开一个进程。", "分配内存");
            return;
        }

        System.Windows.Controls.TextBox sizeInput = new()
        {
            Text = "4096",
            Margin = new Thickness(0, 8, 0, 0)
        };
        StackPanel content = new();
        content.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "请输入要分配的内存大小（字节），属性为 RWX：",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(sizeInput);
        Wpf.Ui.Controls.MessageBox dialog = CreateToolDialog(
            "分配内存",
            content,
            "确定",
            "取消",
            420);
        Wpf.Ui.Controls.MessageBoxResult result = await dialog.ShowDialogAsync();
        if (result != Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            return;
        }

        if (!int.TryParse(
                sizeInput.Text.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int size) ||
            size <= 0)
        {
            await ShowInformationAsync(
                "无效的内存大小，请输入一个正整数。",
                "分配内存");
            return;
        }

        ulong address;
        try
        {
            address = viewModel.AllocateMemory(
                preferredAddress: 0,
                size,
                protection: 0x40);
        }
        catch (Exception exception)
        {
            await ShowInformationAsync(exception.Message, "分配内存失败");
            return;
        }

        if (address == 0)
        {
            int error = Marshal.GetLastWin32Error();
            await ShowInformationAsync(
                $"内存分配失败。\n错误代码: {error} (0x{error:X})",
                "分配内存失败");
            return;
        }

        Wpf.Ui.Controls.MessageBox completion = CreateToolDialog(
            "分配内存成功",
            $"成功分配 {size} 字节内存，地址: 0x{address:X}\n\n是否在反汇编页面中跳转到该地址？",
            "跳转",
            "关闭",
            420);
        Wpf.Ui.Controls.MessageBoxResult completionResult =
            await completion.ShowDialogAsync();
        if (completionResult == Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            await viewModel.NavigateToAddressAsync(address);
        }
    }

    private Wpf.Ui.Controls.MessageBox CreateToolDialog(
        string title,
        object content,
        string primaryButtonText,
        string secondaryButtonText,
        double width)
    {
        return new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButtonText,
            SecondaryButtonText = secondaryButtonText,
            CloseButtonText = string.Empty,
            IsCloseButtonEnabled = false,
            Owner = this,
            Width = width,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
    }

    private Task<Wpf.Ui.Controls.MessageBoxResult> ShowInformationAsync(
        string message,
        string title)
    {
        Wpf.Ui.Controls.MessageBox dialog = CreateToolDialog(
            title,
            message,
            "确定",
            string.Empty,
            420);
        dialog.IsSecondaryButtonEnabled = false;
        return dialog.ShowDialogAsync();
    }

    private async void OpenCreateThread()
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (!viewModel.IsTargetOpen)
        {
            await ShowInformationAsync("请先打开一个进程。", "创建线程");
            return;
        }

        string defaultAddress = viewModel.SelectedInstruction is { Address: not 0 } instruction
            ? $"{instruction.Address:X}"
            : viewModel.AddressInput.Trim();
        System.Windows.Controls.TextBox addressInput = new()
        {
            Text = defaultAddress,
            Margin = new Thickness(0, 8, 0, 0)
        };
        StackPanel content = new();
        content.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "请输入创建线程的起始地址：",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(addressInput);
        Wpf.Ui.Controls.MessageBox dialog = CreateToolDialog(
            "创建线程",
            content,
            "确定",
            "取消",
            420);
        Wpf.Ui.Controls.MessageBoxResult result = await dialog.ShowDialogAsync();
        if (result != Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            return;
        }

        string text = addressInput.Text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        if (!ulong.TryParse(
                text,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out ulong startAddress))
        {
            await ShowInformationAsync(
                "无效的地址格式，请输入十六进制地址。",
                "创建线程");
            return;
        }

        try
        {
            int threadId = viewModel.CreateRemoteThread(
                startAddress,
                parameter: 0,
                creationFlags: 0);
            if (threadId == 0)
            {
                await ShowInformationAsync(
                    "远程线程创建失败。",
                    "创建线程失败");
                return;
            }

            await ShowInformationAsync(
                $"已在地址 0x{startAddress:X} 创建远程线程。",
                "创建线程成功");
        }
        catch (Exception exception)
        {
            await ShowInformationAsync(exception.Message, "创建线程失败");
        }
    }

    private void OpenCallFunction()
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (!viewModel.IsTargetOpen)
        {
            System.Windows.MessageBox.Show(
                this,
                "请先打开一个进程。",
                "Call 函数",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        if (!viewModel.IsTarget64Bit)
        {
            System.Windows.MessageBox.Show(
                this,
                "Call 函数仅支持 64 位进程。",
                "Call 函数",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        CallFunctionWindow window = new(viewModel)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void OpenDllInjection()
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        DllInjectionWindow window = new(viewModel)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private async void OpenTraceConfiguration()
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        TraceConfigurationWindow window = new(viewModel)
        {
            Owner = this
        };
        window.ShowDialog();
        if (window.ShowResultRequested)
        {
            ShowDocument(TraceResultDocument);
        }

        if (window.ContinueTargetRequested)
        {
            await viewModel.ContinueCommand.ExecuteAsync(null);
        }
    }

    private void OnExportTraceClick(object sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not MainViewModel viewModel ||
            viewModel.CurrentTraceSession is not { Steps.Count: > 0 } session)
        {
            System.Windows.MessageBox.Show(
                this,
                "当前没有可导出的追踪结果。",
                "导出追踪结果",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        SaveFileDialog dialog = new()
        {
            Title = "导出追踪结果",
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            DefaultExt = ".txt",
            AddExtension = true,
            FileName = $"追踪结果_{session.StartTime:yyyyMMdd_HHmmss}.txt"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        List<string> lines =
        [
            $"ID: {session.Id}",
            $"开始: {session.StartTime:yyyy-MM-dd HH:mm:ss.fff}",
            $"结束: {session.EndTime:yyyy-MM-dd HH:mm:ss.fff}",
            $"模式: {session.Mode}",
            $"停止原因: {session.StopReason}",
            $"步数: {session.Steps.Count}",
            string.Empty,
            "RIP\t模块偏移\t指令"
        ];
        lines.AddRange(session.Steps.Select(step =>
            $"0x{step.Rip:X}\t{step.ModuleOffsetText}\t{step.DisassemblyText}"));
        File.WriteAllLines(
            dialog.FileName,
            lines,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        System.Windows.MessageBox.Show(
            this,
            $"已导出 {session.Steps.Count:N0} 条追踪结果：\n{dialog.FileName}",
            "导出完成",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }

    private void OnClearTraceClick(object sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.ClearTraceResult();
        }
    }

    private void OpenPointerScan()
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        PointerScanResultWindow window = new(viewModel)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void OpenSignatureSearch()
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        SignatureSearchWindow window = new(viewModel)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void ToggleShortcutCheatSheet()
    {
        ShortcutCheatSheetOverlay.Visibility =
            ShortcutCheatSheetOverlay.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        if (ShortcutCheatSheetOverlay.Visibility == Visibility.Visible)
        {
            ShortcutCheatSheetOverlay.Focus();
        }
    }

    private void OnCloseCheatSheetClick(object sender, RoutedEventArgs eventArgs)
    {
        ShortcutCheatSheetOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnMainWindowPreviewKeyDown(
        object sender,
        KeyEventArgs eventArgs)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control &&
            eventArgs.Key is >= Key.D1 and <= Key.D6)
        {
            SwitchPanelGroup(eventArgs.Key switch
            {
                Key.D1 => PanelGroup.Debugging,
                Key.D2 => PanelGroup.SearchAndAnalysis,
                Key.D3 => PanelGroup.Scripting,
                Key.D4 => PanelGroup.Information,
                Key.D5 => PanelGroup.Extensions,
                _ => PanelGroup.Plugins
            });
            eventArgs.Handled = true;
            return;
        }

        if (ShortcutCheatSheetOverlay.Visibility == Visibility.Visible &&
            eventArgs.Key == Key.Escape)
        {
            ShortcutCheatSheetOverlay.Visibility = Visibility.Collapsed;
            eventArgs.Handled = true;
        }
    }

    private static IReadOnlyList<CheatSheetGroup> CreateCheatSheetGroups() =>
    [
        new(
            "调试",
            SymbolRegular.Bug24,
            [
                new("运行", "F9"),
                new("运行（不处理异常）", "Shift+F9"),
                new("运行到光标", "F4"),
                new("暂停", "F12"),
                new("步入", "F7"),
                new("步过", "F8"),
                new("步出（执行到返回）", "Ctrl+F9"),
                new("切换断点", "F2"),
                new("重新开始", "Ctrl+F2"),
                new("脱离", "Ctrl+Alt+F2")
            ]),
        new(
            "文件",
            SymbolRegular.FolderOpen24,
            [
                new("打开进程…", "Ctrl+P"),
                new("附加到进程…", "Alt+A"),
                new("启动调试…", "F3"),
                new("退出", "Alt+X")
            ]),
        new(
            "面板",
            SymbolRegular.PanelRight24,
            [
                new("下一个面板", "Ctrl+Tab"),
                new("上一个面板", "Ctrl+Shift+Tab"),
                new("关闭当前面板", "Ctrl+W"),
                new("快捷键速查", "Ctrl+Shift+/")
            ]),
        new(
            "内存搜索",
            SymbolRegular.Search24,
            [
                new("首次搜索", "Ctrl+Shift+S"),
                new("再次搜索", "Ctrl+Shift+N")
            ]),
        new(
            "Lua 脚本",
            SymbolRegular.Script24,
            [
                new("运行脚本", "Ctrl+Shift+F11"),
                new("停止脚本", "Ctrl+Shift+F12")
            ])
    ];

    private void AddViewCommand(RoutedCommand command, LayoutDocument document)
    {
        CommandBindings.Add(new CommandBinding(
            command,
            (_, eventArgs) =>
            {
                ShowDocument(document);
                eventArgs.Handled = true;
            }));
    }

    private void AddViewCommand(RoutedCommand command, Action action)
    {
        CommandBindings.Add(new CommandBinding(
            command,
            (_, eventArgs) =>
            {
                action();
                eventArgs.Handled = true;
            }));
    }

    private void ActivateDisassemblySection(
        string contentId,
        string title)
    {
        ShowDocument(DisassemblyDocument);
        LayoutAnchorable? anchorable = DisasmDockManager.Layout
            .Descendents()
            .OfType<LayoutAnchorable>()
            .FirstOrDefault(candidate => string.Equals(
                GetDisassemblyAnchorableId(candidate),
                contentId,
                StringComparison.OrdinalIgnoreCase));
        if (anchorable is null)
        {
            LayoutAnchorablePane? pane = DisasmDockManager.Layout
                .Descendents()
                .OfType<LayoutAnchorablePane>()
                .FirstOrDefault();
            if (pane is null)
            {
                return;
            }

            anchorable = new LayoutAnchorable
            {
                Title = title,
                ContentId = contentId,
                CanClose = false,
                CanHide = false,
                CanFloat = true,
                Content = GetDisassemblySectionContent(contentId)
            };
            pane.Children.Add(anchorable);
        }
        else if (anchorable.Parent is not LayoutAnchorablePane)
        {
            DisasmDockManager.Layout.Hidden.Remove(anchorable);
            LayoutAnchorablePane[] panes = DisasmDockManager.Layout
                .Descendents()
                .OfType<LayoutAnchorablePane>()
                .ToArray();
            LayoutAnchorablePane? pane = panes
                .FirstOrDefault(candidate => !candidate.Children.Any());
            pane ??= panes.FirstOrDefault();
            pane?.Children.Add(anchorable);
        }

        anchorable.IsSelected = true;
        anchorable.IsActive = true;
        DisasmDockManager.UpdateLayout();
    }

    private object GetDisassemblySectionContent(string contentId)
    {
        return contentId switch
        {
            "DisassemblyView" => DisassemblyViewControl,
            "HexView" => HexViewControl,
            "RegisterView" => RegisterViewControl,
            "CallStackView" => CallStackViewControl,
            _ => throw new ArgumentOutOfRangeException(nameof(contentId))
        };
    }

    private void NavigatePanel(int offset)
    {
        LayoutDocumentPane? pane = GetActiveDocumentPane();
        if (pane is null)
        {
            return;
        }

        List<LayoutDocument> documents = pane.Children
            .OfType<LayoutDocument>()
            .ToList();
        if (documents.Count == 0)
        {
            return;
        }

        int index = pane.SelectedContentIndex;
        index = index < 0 || index >= documents.Count
            ? 0
            : (index + offset + documents.Count) % documents.Count;
        ShowDocument(documents[index]);
    }

    private void CloseCurrentPanel()
    {
        LayoutDocumentPane? pane = GetActiveDocumentPane();
        LayoutDocument? document = pane?.SelectedContent as LayoutDocument ??
                                   pane?.Children
                                       .OfType<LayoutDocument>()
                                       .FirstOrDefault(static candidate => candidate.IsActive);
        if (pane is null || document is null)
        {
            return;
        }

        if (IsLastDocumentThatCannotClose(document, pane) ||
            !CanHidePanel(document) ||
            string.IsNullOrWhiteSpace(document.ContentId))
        {
            System.Windows.MessageBox.Show(
                this,
                "该面板不能关闭",
                "关闭面板",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        int index = pane.Children.IndexOf(document);
        if (index < 0)
        {
            return;
        }

        _hiddenDocuments[document.ContentId] = new HiddenDocumentState(document, pane, index);
        pane.RemoveChild(document);
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            () => EnsureDocumentSelection(pane));
    }

    private static bool IsLastDocumentThatCannotClose(
        LayoutDocument document,
        LayoutDocumentPane pane)
    {
        if (document.CanClose)
        {
            return false;
        }

        return pane.Children.OfType<LayoutDocument>().Count() == 1;
    }

    private static bool CanHidePanel(LayoutDocument document)
    {
        return !string.Equals(
            document.ContentId,
            "SourceDebugPanel",
            StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureDocumentSelection(LayoutDocumentPane pane)
    {
        if (pane.Children.Count == 0 || pane.SelectedContentIndex >= 0)
        {
            return;
        }

        pane.SelectedContentIndex = 0;
    }

    private LayoutDocumentPane? GetActiveDocumentPane()
    {
        return DockManager.Layout
            .Descendents()
            .OfType<LayoutDocumentPane>()
            .FirstOrDefault();
    }

    private void RegisterDebuggerCommands()
    {
        CommandBindings.Add(new CommandBinding(
            DebuggerCommands.Run,
            (_, args) => ExecuteViewModelCommand(args, static viewModel => viewModel.ContinueCommand),
            (_, args) => CanExecuteViewModelCommand(args, static viewModel => viewModel.ContinueCommand)));
        CommandBindings.Add(new CommandBinding(
            DebuggerCommands.Pause,
            (_, args) => ExecuteViewModelCommand(args, static viewModel => viewModel.BreakCommand),
            (_, args) => CanExecuteViewModelCommand(args, static viewModel => viewModel.BreakCommand)));
        CommandBindings.Add(new CommandBinding(
            DebuggerCommands.PassException,
            (_, args) => ExecuteViewModelCommand(args, static viewModel => viewModel.PassExceptionCommand),
            (_, args) => CanExecuteViewModelCommand(args, static viewModel => viewModel.PassExceptionCommand)));
        CommandBindings.Add(new CommandBinding(
            DebuggerCommands.Detach,
            (_, args) => ExecuteViewModelCommand(args, static viewModel => viewModel.DetachCommand),
            (_, args) => CanExecuteViewModelCommand(args, static viewModel => viewModel.DetachCommand)));
        CommandBindings.Add(new CommandBinding(
            DebuggerCommands.StepInto,
            (_, args) => ExecuteViewModelCommand(args, static viewModel => viewModel.StepIntoCommand),
            (_, args) => CanExecuteViewModelCommand(args, static viewModel => viewModel.StepIntoCommand)));
        CommandBindings.Add(new CommandBinding(
            DebuggerCommands.StepOver,
            (_, args) => ExecuteViewModelCommand(args, static viewModel => viewModel.StepOverCommand),
            (_, args) => CanExecuteViewModelCommand(args, static viewModel => viewModel.StepOverCommand)));
        CommandBindings.Add(new CommandBinding(
            DebuggerCommands.StepOut,
            (_, args) => ExecuteViewModelCommand(args, static viewModel => viewModel.StepOutCommand),
            (_, args) => CanExecuteViewModelCommand(args, static viewModel => viewModel.StepOutCommand)));
        CommandBindings.Add(new CommandBinding(
            DebuggerCommands.RunToCursor,
            (_, args) => ExecuteViewModelCommand(args, static viewModel => viewModel.RunToCursorCommand),
            (_, args) => CanExecuteViewModelCommand(args, static viewModel => viewModel.RunToCursorCommand)));
        CommandBindings.Add(new CommandBinding(
            DebuggerCommands.Restart,
            (_, args) => ExecuteViewModelCommand(args, static viewModel => viewModel.RestartCommand),
            (_, args) => CanExecuteViewModelCommand(args, static viewModel => viewModel.RestartCommand)));
        CommandBindings.Add(new CommandBinding(
            DebuggerCommands.ToggleBreakpoint,
            (_, args) => ExecuteViewModelCommand(args, static viewModel => viewModel.ToggleBreakpointCommand),
            (_, args) => CanExecuteViewModelCommand(args, static viewModel => viewModel.ToggleBreakpointCommand)));

        InputBindings.Add(new KeyBinding(DebuggerCommands.Run, Key.F9, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(DebuggerCommands.PassException, Key.F9, ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(DebuggerCommands.RunToCursor, Key.F4, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(DebuggerCommands.Pause, Key.F12, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(DebuggerCommands.StepInto, Key.F7, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(DebuggerCommands.StepOver, Key.F8, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(DebuggerCommands.StepOut, Key.F9, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(DebuggerCommands.ToggleBreakpoint, Key.F2, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(DebuggerCommands.Restart, Key.F2, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(DebuggerCommands.Detach, Key.F2, ModifierKeys.Control | ModifierKeys.Alt));
    }

    private void ExecuteViewModelCommand(
        ExecutedRoutedEventArgs eventArgs,
        Func<MainViewModel, ICommand> commandSelector)
    {
        if (DataContext is MainViewModel viewModel)
        {
            ICommand command = commandSelector(viewModel);
            if (command.CanExecute(null))
            {
                command.Execute(null);
            }
        }

        eventArgs.Handled = true;
    }

    private void CanExecuteViewModelCommand(
        CanExecuteRoutedEventArgs eventArgs,
        Func<MainViewModel, ICommand> commandSelector)
    {
        eventArgs.CanExecute = DataContext is MainViewModel viewModel &&
                               commandSelector(viewModel).CanExecute(null);
        eventArgs.Handled = true;
    }

    private void OnExitClick(object sender, RoutedEventArgs eventArgs)
    {
        Application.Current.Shutdown();
    }

    private void OnClearLogClick(object sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.LogMessages.Clear();
        }
    }

    private void OnResetLayoutClick(object sender, RoutedEventArgs eventArgs)
    {
        ShowDocument(MemorySearchDocument);
    }

    private void OnShowMemorySearchClick(object sender, RoutedEventArgs eventArgs) =>
        ShowDocument(MemorySearchDocument);

    private void OnShowDisassemblyClick(object sender, RoutedEventArgs eventArgs) =>
        ActivateDisassemblySection("DisassemblyView", "反汇编");

    private void OnShowHexClick(object sender, RoutedEventArgs eventArgs) =>
        ActivateDisassemblySection("HexView", "数据视图");

    private void OnShowRegistersClick(object sender, RoutedEventArgs eventArgs) =>
        ActivateDisassemblySection("RegisterView", "寄存器");

    private void OnShowCallStackClick(object sender, RoutedEventArgs eventArgs) =>
        ActivateDisassemblySection("CallStackView", "调用堆栈");

    private void OnShowModulesClick(object sender, RoutedEventArgs eventArgs) =>
        ShowDocument(ModulesDocument);

    private void OnShowThreadsClick(object sender, RoutedEventArgs eventArgs) =>
        ShowDocument(ThreadsDocument);

    private void OnShowMemoryMapClick(object sender, RoutedEventArgs eventArgs) =>
        ShowDocument(MemoryMapDocument);

    private void OnShowHandlesClick(object sender, RoutedEventArgs eventArgs) =>
        ShowDocument(HandlesDocument);

    private void OnShowBreakpointsClick(object sender, RoutedEventArgs eventArgs) =>
        ShowDocument(BreakpointsDocument);

    private void OnShowCrossReferencesClick(object sender, RoutedEventArgs eventArgs) =>
        ShowDocument(CrossReferencesDocument);

    private void OnShowStringReferencesClick(object sender, RoutedEventArgs eventArgs) =>
        ShowDocument(StringReferencesDocument);

    private void OnShowRttiClick(object sender, RoutedEventArgs eventArgs) =>
        ShowDocument(RttiDocument);

    private void OnShowUnrealClick(object sender, RoutedEventArgs eventArgs) =>
        ShowDocument(UnrealExplorerDocument);

    private void OnShowExceptionsClick(object sender, RoutedEventArgs eventArgs) =>
        ShowDocument(ExceptionsDocument);

    private void OnShowNotesClick(object sender, RoutedEventArgs eventArgs) =>
        ShowDocument(NotesDocument);

    private void OnShowLogClick(object sender, RoutedEventArgs eventArgs) =>
        ShowDocument(LogDocument);

    private void OnShowAutoAssemblerClick(object sender, RoutedEventArgs eventArgs) =>
        ShowDocument(AutoAssemblerDocument);

    private void OnShowLuaClick(object sender, RoutedEventArgs eventArgs) =>
        ShowDocument(LuaDocument);

    private void ShowDocument(LayoutDocument document)
    {
        if (document.Content is null &&
            string.Equals(
                document.ContentId,
                "SourceDebugPanel",
                StringComparison.OrdinalIgnoreCase))
        {
            document.Content = new UI.Views.Panels.SourceDebugView();
        }

        if (document.Content is FrameworkElement contentElement &&
            contentElement.DataContext is null)
        {
            contentElement.DataContext =
                contentElement is UI.Views.Panels.SourceDebugView &&
                DataContext is MainViewModel viewModel
                    ? viewModel.SourceDebugPanel
                    : DataContext;
        }

        LayoutDocumentPane? activePane = GetActiveDocumentPane();
        if (document.Parent is LayoutDocumentPane currentPane &&
            activePane is not null &&
            !ReferenceEquals(currentPane, activePane))
        {
            currentPane.RemoveChild(document);
        }

        WriteDiagnostic(
            $"ShowDocument {document.Title} " +
            $"id={document.ContentId ?? "<null>"} " +
            $"parent={(document.Parent is null ? "null" : "set")} " +
            $"group={_activePanelGroup}");
        string? contentId = document.ContentId;
        HiddenDocumentState? hiddenState = null;
        if (!string.IsNullOrWhiteSpace(contentId))
        {
            _knownPanelDocuments[contentId] = document;
            _materializedPanelIds.Add(contentId);
            _hiddenDocuments.Remove(contentId, out hiddenState);
        }

        PanelGroup group = ResolveGroup(document);
        if (_activePanelGroup != group)
        {
            SwitchPanelGroup(group, document);
        }
        else if (document.Parent is null)
        {
            LayoutDocumentPane? pane = hiddenState?.Pane;
            if (pane?.Parent is null)
            {
                pane = GetActiveDocumentPane();
            }

            if (pane is not null)
            {
                int index = Math.Clamp(
                    hiddenState?.Index ?? pane.Children.Count,
                    0,
                    pane.Children.Count);
                pane.InsertChildAt(index, document);
            }
        }

        document.IsSelected = true;
        document.IsActive = true;
        WriteDiagnostic(
            $"ShowDocument complete {document.Title} " +
            $"parent={(document.Parent is null ? "null" : "set")} " +
            $"group={_activePanelGroup}");
    }

    private static void WriteDiagnostic(string message)
    {
        if (string.IsNullOrWhiteSpace(DiagnosticLogPath))
        {
            return;
        }

        try
        {
            File.AppendAllText(
                DiagnosticLogPath,
                $"{DateTime.Now:O} {message}{Environment.NewLine}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
        }
    }

    private void RestoreHiddenDocuments()
    {
        foreach (HiddenDocumentState hidden in _hiddenDocuments.Values)
        {
            int index = Math.Clamp(hidden.Index, 0, hidden.Pane.Children.Count);
            hidden.Pane.InsertChildAt(index, hidden.Document);
        }

        _hiddenDocuments.Clear();
    }

    private sealed record CheatSheetGroup(
        string Name,
        SymbolRegular Icon,
        IReadOnlyList<CheatSheetEntry> Entries);

    private sealed record CheatSheetEntry(
        string Name,
        string Gesture);

    private sealed record HiddenDocumentState(
        LayoutDocument Document,
        LayoutDocumentPane Pane,
        int Index);

    private sealed record DetachedPanelDocument(
        LayoutDocument Document,
        LayoutDocumentPane Pane,
        int Index);

    private sealed record PluginHotkeyBinding(
        CommandBinding CommandBinding,
        KeyBinding KeyBinding);
}
