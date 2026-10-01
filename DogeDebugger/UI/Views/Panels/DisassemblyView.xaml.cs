using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DogeDebugger.Core.Disassembly;
using DogeDebugger.Core.Modules;
using DogeDebugger.Core.Signatures;
using DogeDebugger.Debugger.Breakpoints;
using DogeDebugger.UI.Views.Dialogs;

namespace DogeDebugger.UI.Views.Panels;

public partial class DisassemblyView : UserControl
{
    public DisassemblyView()
    {
        InitializeComponent();
        InstructionGrid.ContextMenu =
            (ContextMenu)FindResource("SingleLineContextMenu");
        InstructionGrid.ContextMenuOpening += OnInstructionGridContextMenuOpening;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private IReadOnlyList<InstructionSnapshot> SelectedInstructions =>
        InstructionGrid.SelectedItems
            .OfType<InstructionSnapshot>()
            .ToArray();

    private InstructionSnapshot? PrimaryInstruction =>
        InstructionGrid.SelectedItem as InstructionSnapshot ??
        SelectedInstructions.FirstOrDefault();

    private async void OnInstructionGridDoubleClick(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (ViewModel is { } viewModel &&
            PrimaryInstruction is { } instruction)
        {
            await viewModel.NavigateToAddressAsync(instruction.Address);
        }
    }

    private void OnInstructionGridPreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.OriginalSource is not DependencyObject source)
        {
            return;
        }

        DataGridRow? row = ItemsControl.ContainerFromElement(
            InstructionGrid,
            source) as DataGridRow;
        if (row is not null && !row.IsSelected)
        {
            InstructionGrid.SelectedItems.Clear();
            row.IsSelected = true;
        }

        UpdateInstructionGridContextMenu();
    }

    private void OnInstructionGridContextMenuOpening(
        object sender,
        ContextMenuEventArgs eventArgs)
    {
        UpdateInstructionGridContextMenu();
    }

    private void UpdateInstructionGridContextMenu()
    {
        InstructionGrid.ContextMenu = (ContextMenu)FindResource(
            InstructionGrid.SelectedItems.Count > 1
                ? "MultiLineContextMenu"
                : "SingleLineContextMenu");
    }

    private async void OnJumpToAddressClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        string initial = PrimaryInstruction is { } instruction
            ? $"0x{instruction.Address:X}"
            : "0x";
        TextInputDialog dialog = new(
            "跳转到地址",
            "输入地址、模块+偏移或符号：",
            initial)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true)
        {
            await viewModel.NavigateToAddressExpressionAsync(dialog.Value);
        }
    }

    private async void OnFollowBranchClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is { } viewModel)
        {
            await viewModel.FollowSelectedBranchAsync();
        }
    }

    private async void OnGoBackClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is { } viewModel)
        {
            await viewModel.GoBackCommand.ExecuteAsync(null);
        }
    }

    private void OnToggleBookmarkClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is { } viewModel &&
            sender is MenuItem { Tag: string indexText } &&
            int.TryParse(indexText, out int index))
        {
            viewModel.ToggleBookmark(index);
            UpdateBookmarkMenus((ContextMenu)FindResource("SingleLineContextMenu"));
        }
    }

    private async void OnGoToBookmarkClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is { } viewModel &&
            sender is MenuItem { Tag: string indexText } &&
            int.TryParse(indexText, out int index))
        {
            await viewModel.GoToBookmarkAsync(index);
        }
    }

    private async void OnAssembleClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel ||
            PrimaryInstruction is not { } instruction)
        {
            return;
        }

        AssembleInstructionDialog dialog = new(
            instruction.Address,
            instruction.Text)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        (bool success, string message) =
            await viewModel.AssembleSelectedInstructionAsync(dialog.InstructionText);
        if (!success)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                message,
                "汇编错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void OnNopFillClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is { } viewModel)
        {
            await viewModel.NopFillSelectedAsync();
        }
    }

    private async void OnUndoNopClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is { } viewModel)
        {
            await viewModel.UndoNopFillAsync();
        }
    }

    private void OnEditCommentClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel ||
            PrimaryInstruction is not { } instruction)
        {
            return;
        }

        TextInputDialog dialog = new(
            $"编辑注释 - 0x{instruction.Address:X}",
            "输入注释内容...",
            viewModel.GetInstructionComment(instruction.Address),
            width: 480)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true)
        {
            viewModel.SetInstructionComment(instruction.Address, dialog.Value);
        }
    }

    private void OnEditCustomSymbolClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel ||
            PrimaryInstruction is not { } instruction)
        {
            return;
        }

        TextInputDialog dialog = new(
            $"自定义符号 - 0x{instruction.Address:X}",
            "输入符号名（留空删除）...",
            viewModel.GetInstructionCustomSymbol(instruction.Address),
            width: 480)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true)
        {
            viewModel.SetInstructionCustomSymbol(
                instruction.Address,
                dialog.Value);
        }
    }

    private void OnDeleteCustomSymbolClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is { } viewModel &&
            PrimaryInstruction is { } instruction)
        {
            viewModel.RemoveInstructionCustomSymbol(instruction.Address);
        }
    }

    private void OnToggleBreakpointClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        ViewModel?.ToggleBreakpointAtSelected();
    }

    private void OnDeleteBreakpointClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        ViewModel?.DeleteBreakpointAtSelected();
    }

    private void OnHardwareBreakpointClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel ||
            sender is not MenuItem { Tag: string kindText } ||
            !Enum.TryParse(kindText, out BreakpointKind kind))
        {
            return;
        }

        viewModel.AddHardwareBreakpoint(kind, string.Empty);
    }

    private void OnHardwareBreakpointWithConditionClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel ||
            PrimaryInstruction is not { } instruction ||
            sender is not MenuItem { Tag: string kindText } ||
            !Enum.TryParse(kindText, out BreakpointKind kind))
        {
            return;
        }

        ConditionBreakpointDialog dialog = new(
            instruction.Address,
            string.Empty,
            viewModel.ValidateBreakpointCondition)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true)
        {
            viewModel.AddHardwareBreakpoint(kind, dialog.Condition);
        }
    }

    private void OnEditConditionClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel ||
            PrimaryInstruction is not { } instruction)
        {
            return;
        }

        ConditionBreakpointDialog dialog = new(
            instruction.Address,
            viewModel.GetBreakpointCondition(instruction.Address),
            viewModel.ValidateBreakpointCondition)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!viewModel.HasBreakpointAtSelectedAddress &&
            !viewModel.ToggleBreakpointAtSelected())
        {
            return;
        }

        viewModel.UpdateBreakpointCondition(
            instruction.Address,
            dialog.Condition);
    }

    private void OnSetInstructionPointerClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        ViewModel?.SetSelectedInstructionPointer();
    }

    private void OnCopyClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel ||
            sender is not MenuItem { Tag: string copyKind })
        {
            return;
        }

        string text = FormatCopyText(
            copyKind,
            SelectedInstructions,
            viewModel);
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
            viewModel.StatusText = $"已复制 {SelectedInstructions.Count} 行。";
        }
        catch (Exception exception)
        {
            viewModel.StatusText = exception.Message;
        }
    }

    private async void OnSearchInstructionClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (Window.GetWindow(this) is MainWindow mainWindow)
        {
            mainWindow.OpenInstructionSearchWindow();
            return;
        }

        await Task.CompletedTask;
    }

    private async void OnFindReferencesClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (sender is MenuItem
            {
                Tag: InstructionReferenceTarget target
            })
        {
            await RunReferenceSearchAsync(target);
        }
    }

    private void OnFindInstructionAccessClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        MainWindow.TraceUiInteraction("FindInstructionAccess click");
        MainWindow? mainWindow =
            Window.GetWindow(this) as MainWindow ??
            Application.Current?.MainWindow as MainWindow;
        if (ViewModel is not { } viewModel)
        {
            MainWindow.TraceUiInteraction("FindInstructionAccess viewModel null");
            return;
        }

        if (PrimaryInstruction is not { } instruction)
        {
            MainWindow.TraceUiInteraction(
                "FindInstructionAccess primary instruction null");
            return;
        }

        if (sender is not MenuItem
            {
                Tag: InstructionMemoryOperand operand
            })
        {
            MainWindow.TraceUiInteraction(
                $"FindInstructionAccess tag invalid sender={sender?.GetType().FullName}");
            return;
        }

        if (mainWindow is null)
        {
            MainWindow.TraceUiInteraction("FindInstructionAccess main window null");
            return;
        }

        mainWindow.OpenInstructionAccessWatch(
            viewModel,
            instruction,
            operand);
    }

    private async void OnPreviewKeyDown(
        object sender,
        KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.R &&
            Keyboard.Modifiers == ModifierKeys.Control)
        {
            eventArgs.Handled = true;
            InstructionReferenceTarget? target = GetReferenceTargets()
                .FirstOrDefault();
            if (target is not null)
            {
                await RunReferenceSearchAsync(target);
            }

            return;
        }

        if (eventArgs.Key == Key.G &&
            Keyboard.Modifiers == ModifierKeys.Control)
        {
            eventArgs.Handled = true;
            OnJumpToAddressClick(this, new RoutedEventArgs());
        }
    }

    private async Task RunReferenceSearchAsync(
        InstructionReferenceTarget target)
    {
        if (ViewModel is not { } viewModel ||
            PrimaryInstruction is not { } instruction ||
            Window.GetWindow(this) is not MainWindow mainWindow)
        {
            return;
        }

        ModuleDescriptor? module =
            viewModel.FindModuleForAddress(instruction.Address);
        if (module is null)
        {
            viewModel.StatusText = "当前指令不属于任何已加载模块。";
            return;
        }

        mainWindow.ShowInstructionSearchPanel("引用搜索");
        await mainWindow.RunInstructionReferenceSearchAsync(
            viewModel,
            new InstructionReferenceSearchRequest
            {
                Value = target.Value,
                Kind = target.Kind,
                Bitness = viewModel.IsTarget64Bit ? 64 : 32,
                SyntaxFormat = viewModel.DisassemblySyntax,
                ScanModules = [module]
            });
    }

    private IReadOnlyList<InstructionReferenceTarget> GetReferenceTargets() =>
        PrimaryInstruction is { } instruction && ViewModel is { } viewModel
            ? InstructionReferenceTargetBuilder.Build(
                instruction,
                viewModel.IsTarget64Bit ? 64 : 32,
                viewModel.Modules)
            : [];

    private async void OnAnalyzeFunctionClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        await AnalyzeSelectedAddressAsync(showPanel: true);
    }

    private async void OnSelectCurrentFunctionClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel ||
            await viewModel.GetSelectedFunctionRangeAsync() is not { } range)
        {
            return;
        }

        InstructionGrid.SelectedItems.Clear();
        InstructionSnapshot? first = null;
        foreach (InstructionSnapshot instruction in viewModel.Disassembly)
        {
            if (instruction.Address < range.Start ||
                instruction.Address >= range.End)
            {
                continue;
            }

            first ??= instruction;
            InstructionGrid.SelectedItems.Add(instruction);
        }

        if (first is not null)
        {
            InstructionGrid.ScrollIntoView(first);
            viewModel.StatusText =
                $"已选中函数 0x{range.Start:X} - 0x{range.End:X}。";
        }
    }

    private async void OnGoToFunctionStartClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        await NavigateToFunctionBoundaryAsync(start: true);
    }

    private async void OnGoToFunctionEndClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        await NavigateToFunctionBoundaryAsync(start: false);
    }

    private async Task NavigateToFunctionBoundaryAsync(bool start)
    {
        if (ViewModel is not { } viewModel ||
            await viewModel.GetSelectedFunctionRangeAsync() is not { } range)
        {
            return;
        }

        ulong address = start ? range.Start : range.End;
        await viewModel.NavigateToAddressAsync(address);
        viewModel.StatusText = start
            ? $"已跳转到函数头 0x{address:X}。"
            : $"已跳转到函数尾 0x{address:X}。";
    }

    private async Task AnalyzeSelectedAddressAsync(bool showPanel)
    {
        if (ViewModel is not { } viewModel ||
            PrimaryInstruction is not { } instruction)
        {
            return;
        }

        ModuleDescriptor? module = viewModel.FindModuleForAddress(instruction.Address);
        if (module is null)
        {
            viewModel.StatusText = "当前地址不在任何已加载模块中。";
            return;
        }

        bool analyzed = await viewModel.AnalyzeSelectedAddressAsync(module);
        if (analyzed && showPanel &&
            Window.GetWindow(this) is MainWindow mainWindow)
        {
            mainWindow.ShowCrossReferencesPanel();
        }
    }

    private void OnSearchSignatureClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (Window.GetWindow(this) is MainWindow mainWindow)
        {
            mainWindow.OpenSignatureSearchWindow();
        }
    }

    private async void OnGenerateSignatureClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        await GenerateSignatureAsync(searchAfterGeneration: false);
    }

    private async void OnGenerateAndSearchSignatureClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        await GenerateSignatureAsync(searchAfterGeneration: true);
    }

    private Task GenerateSignatureAsync(bool searchAfterGeneration)
    {
        if (ViewModel is not { } viewModel)
        {
            return Task.CompletedTask;
        }

        AobSignature? signature =
            viewModel.GenerateSignatureForInstructions(SelectedInstructions);
        if (signature is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            Clipboard.SetText(signature.Pattern);
        }
        catch
        {
        }

        if (searchAfterGeneration &&
            Window.GetWindow(this) is MainWindow mainWindow)
        {
            mainWindow.OpenSignatureSearchWindow();
        }

        return Task.CompletedTask;
    }

    private void OnAddressModeClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel ||
            sender is not MenuItem { Tag: string modeText } ||
            !Enum.TryParse(modeText, out AssemblyAddressMode mode))
        {
            return;
        }

        viewModel.DisassemblyAddressMode = mode;
    }

    private void OnToggleShowBytesClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.ShowDisassemblyBytes = !viewModel.ShowDisassemblyBytes;
            ApplyBytesColumnVisibility(viewModel);
        }
    }

    private void OnBytesStyleClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel ||
            sender is not MenuItem { Tag: string styleText } ||
            !Enum.TryParse(styleText, out DisassemblyBytesStyle style))
        {
            return;
        }

        viewModel.DisassemblyBytesStyle = style;
    }

    private void OnSyntaxClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is not { } viewModel ||
            sender is not MenuItem { Tag: string syntaxText } ||
            !Enum.TryParse(syntaxText, out AssemblySyntax syntax))
        {
            return;
        }

        viewModel.DisassemblySyntax = syntax;
    }

    private void OnToggleSignedImmediateClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.UseSignedImmediateOperands =
                !viewModel.UseSignedImmediateOperands;
        }
    }

    private void OnToggleJumpArrowsClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.ShowJumpArrows = !viewModel.ShowJumpArrows;
        }
    }

    private void OnSingleLineContextMenuOpened(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (sender is not ContextMenu menu || ViewModel is not { } viewModel)
        {
            return;
        }

        UpdateBookmarkMenus(menu);
        SetEnabled(menu, "NOP 填充", !viewModel.IsSelectedInstructionNop);
        SetEnabled(menu, "删除断点", viewModel.HasBreakpointAtSelectedAddress);
        SetEnabled(
            menu,
            "在此设置",
            viewModel.CanSetSelectedInstructionPointer);
        bool hasCustomSymbol =
            PrimaryInstruction is { } instruction &&
            viewModel.GetInstructionCustomSymbol(instruction.Address).Length > 0;
        SetEnabled(menu, "删除自定义符号", hasCustomSymbol);
        SetVisibility(
            menu,
            "删除自定义符号",
            hasCustomSymbol ? Visibility.Visible : Visibility.Collapsed);
        SetHeader(menu, "在此设置", $"在此设置{viewModel.InstructionPointerName}");
        UpdateInstructionAccessMenu(menu, viewModel);
        UpdateFindReferencesMenu(menu, viewModel);
        SetHeader(
            menu,
            "切换断点",
            viewModel.HasBreakpointAtSelectedAddress
                ? "删除断点(T)"
                : "切换断点(T)");
        SetVisibility(
            menu,
            "撤销 NOP",
            viewModel.CanUndoNopFill ? Visibility.Visible : Visibility.Collapsed);

        SetChecked(menu, "绝对地址", viewModel.DisassemblyAddressMode == AssemblyAddressMode.Absolute);
        SetChecked(menu, "RVA", viewModel.DisassemblyAddressMode == AssemblyAddressMode.Rva);
        SetChecked(menu, "模块+偏移", viewModel.DisassemblyAddressMode == AssemblyAddressMode.ModuleOffset);
        SetChecked(menu, "显示字节码", viewModel.ShowDisassemblyBytes);
        SetChecked(menu, "x64dbg", viewModel.DisassemblyBytesStyle == DisassemblyBytesStyle.X64Dbg);
        SetChecked(menu, "Cheat Engine", viewModel.DisassemblyBytesStyle == DisassemblyBytesStyle.CheatEngine);
        SetChecked(menu, "Intel", viewModel.DisassemblySyntax == AssemblySyntax.Intel);
        SetChecked(menu, "MASM", viewModel.DisassemblySyntax == AssemblySyntax.Masm);
        SetChecked(menu, "NASM", viewModel.DisassemblySyntax == AssemblySyntax.Nasm);
        SetChecked(menu, "GAS", viewModel.DisassemblySyntax == AssemblySyntax.Gas);
        SetChecked(menu, "立即数带符号显示", viewModel.UseSignedImmediateOperands);
        SetChecked(menu, "显示跳转箭头", viewModel.ShowJumpArrows);
        ApplyBytesColumnVisibility(viewModel);
    }

    private void OnMultiLineContextMenuOpened(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (sender is not ContextMenu menu ||
            ViewModel is not { } viewModel)
        {
            return;
        }

        MenuItem? header = FindMenuItem(menu, "已选择");
        if (header is not null)
        {
            header.Header = $"已选择 {SelectedInstructions.Count} 行";
        }

        SetVisibility(
            menu,
            "撤销 NOP",
            viewModel.CanUndoNopFill ? Visibility.Visible : Visibility.Collapsed);
    }

    private void OnAiAnalyzeSelectedCodeClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        IReadOnlyList<InstructionSnapshot> instructions = SelectedInstructions;
        if (instructions.Count == 0 ||
            Window.GetWindow(this) is not MainWindow mainWindow)
        {
            return;
        }

        string code = string.Join(
            Environment.NewLine,
            instructions.Select(static instruction =>
                $"{instruction.Address:X16}  {instruction.BytesText}  {instruction.Text}"));
        mainWindow.OpenAiAssistantWithPrompt(
            "请分析以下汇编代码，说明控制流、寄存器与内存访问、关键数据以及潜在风险："
            + Environment.NewLine
            + Environment.NewLine
            + code);
    }

    private void ApplyBytesColumnVisibility(MainViewModel viewModel)
    {
        BytesColumn.Visibility = viewModel.ShowDisassemblyBytes
            ? Visibility.Visible
            : Visibility.Collapsed;
        ArrowColumn.Visibility = viewModel.ShowJumpArrows
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static string FormatCopyText(
        string copyKind,
        IReadOnlyList<InstructionSnapshot> instructions,
        MainViewModel viewModel)
    {
        IEnumerable<string> lines = instructions.Select(instruction =>
            copyKind switch
            {
                "Address" => instruction.AddressText,
                "AbsoluteAddress" => $"0x{instruction.Address:X}",
                "Rva" => FormatRva(instruction, viewModel),
                "Disassembly" => instruction.Text,
                "Bytes" => instruction.BytesText,
                "Line" =>
                    $"{instruction.AddressText} {instruction.BytesText} {instruction.Text}",
                _ => string.Empty
            });
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatRva(
        InstructionSnapshot instruction,
        MainViewModel viewModel)
    {
        ModuleDescriptor? module =
            viewModel.FindModuleForAddress(instruction.Address);
        return module is null
            ? $"0x{instruction.Address:X}"
            : $"0x{instruction.Address - module.BaseAddress:X}";
    }

    private void UpdateBookmarkMenus(ContextMenu menu)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        MenuItem? setMenu = FindMenuItem(menu, "设置/取消 书签");
        MenuItem? goToMenu = FindMenuItem(menu, "跳转书签");
        if (setMenu is null || goToMenu is null)
        {
            return;
        }

        for (int index = 0; index < 10; index++)
        {
            MenuItem setItem = (MenuItem)setMenu.Items[index];
            MenuItem goToItem = (MenuItem)goToMenu.Items[index];
            ulong? bookmark = viewModel.GetBookmark(index);
            setItem.IsCheckable = true;
            setItem.IsChecked =
                bookmark is { } bookmarkAddress &&
                PrimaryInstruction is { } instruction &&
                bookmarkAddress == instruction.Address;
            setItem.Header = bookmark is { } setAddress
                ? $"书签 {index}: {FormatAddress(viewModel, setAddress)}"
                : $"书签 {index}";
            goToItem.IsEnabled = bookmark is not null;
            goToItem.Header = bookmark is { } goAddress
                ? $"书签 {index}: {FormatAddress(viewModel, goAddress)}"
                : $"书签 {index}";
        }
    }

    private void UpdateFindReferencesMenu(
        ContextMenu menu,
        MainViewModel viewModel)
    {
        MenuItem? findMenu = FindMenuItem(menu, "查找引用");
        if (findMenu is null)
        {
            return;
        }

        findMenu.Items.Clear();
        findMenu.Tag = null;
        IReadOnlyList<InstructionReferenceTarget> targets =
            PrimaryInstruction is { } instruction
                ? InstructionReferenceTargetBuilder.Build(
                    instruction,
                    viewModel.IsTarget64Bit ? 64 : 32,
                    viewModel.Modules)
                : [];
        if (targets.Count == 0)
        {
            findMenu.Header = "查找引用(_R)";
            findMenu.IsEnabled = false;
            return;
        }

        findMenu.IsEnabled = true;
        if (targets.Count == 1)
        {
            findMenu.Header = $"查找引用：{targets[0].DisplayText}";
            findMenu.Tag = targets[0];
            return;
        }

        findMenu.Header = "查找引用";
        foreach (InstructionReferenceTarget target in targets)
        {
            MenuItem child = new()
            {
                Header = target.DisplayText,
                Tag = target
            };
            child.Click += OnFindReferencesClick;
            findMenu.Items.Add(child);
        }
    }

    private void UpdateInstructionAccessMenu(
        ContextMenu menu,
        MainViewModel viewModel)
    {
        MenuItem? accessMenu = FindMenuItem(
            menu,
            "找出指令访问的地址");
        if (accessMenu is null)
        {
            return;
        }

        accessMenu.Items.Clear();
        accessMenu.Tag = null;
        IReadOnlyList<InstructionMemoryOperand> operands =
            PrimaryInstruction is { } instruction
                ? InstructionMemoryOperandBuilder.Build(
                    instruction,
                    viewModel.IsTarget64Bit ? 64 : 32,
                    viewModel.DisassemblySyntax,
                    viewModel.Modules)
                : [];
        if (operands.Count == 0)
        {
            accessMenu.Visibility = Visibility.Collapsed;
            accessMenu.IsEnabled = false;
            return;
        }

        accessMenu.Visibility = Visibility.Visible;
        accessMenu.IsEnabled = true;
        if (operands.Count == 1)
        {
            accessMenu.Header =
                $"找出指令访问的地址：{operands[0].DisplayText}";
            accessMenu.Tag = operands[0];
            return;
        }

        accessMenu.Header = "找出指令访问的地址";
        foreach (InstructionMemoryOperand operand in operands)
        {
            MenuItem child = new()
            {
                Header = operand.DisplayText,
                Tag = operand
            };
            child.Click += OnFindInstructionAccessClick;
            accessMenu.Items.Add(child);
        }
    }

    private static string FormatAddress(
        MainViewModel viewModel,
        ulong address)
    {
        ModuleDescriptor? module = viewModel.FindModuleForAddress(address);
        return module is null
            ? $"0x{address:X}"
            : $"{module.Name}+{address - module.BaseAddress:X}";
    }

    private static MenuItem? FindMenuItem(ItemsControl root, string headerPrefix)
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

    private static void SetEnabled(
        ContextMenu menu,
        string headerPrefix,
        bool enabled)
    {
        MenuItem? item = FindMenuItem(menu, headerPrefix);
        if (item is not null)
        {
            item.IsEnabled = enabled;
        }
    }

    private static void SetHeader(
        ContextMenu menu,
        string headerPrefix,
        string header)
    {
        MenuItem? item = FindMenuItem(menu, headerPrefix);
        if (item is not null)
        {
            item.Header = header;
        }
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

    private static void SetVisibility(
        ContextMenu menu,
        string headerPrefix,
        Visibility visibility)
    {
        MenuItem? item = FindMenuItem(menu, headerPrefix);
        if (item is not null)
        {
            item.Visibility = visibility;
        }
    }
}
