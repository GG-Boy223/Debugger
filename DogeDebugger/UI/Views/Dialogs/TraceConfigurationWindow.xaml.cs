using System.Globalization;
using System.Windows;
using DogeDebugger.Core.Trace;
using Wpf.Ui.Controls;

namespace DogeDebugger.UI.Views.Dialogs;

public partial class TraceConfigurationWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;
    private bool _isTracing;
    private bool _isCompleted;

    public TraceConfigurationWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        _viewModel.TraceProgressChanged += OnTraceProgressChanged;
        _viewModel.TraceCompleted += OnTraceCompleted;
        Closed += (_, _) =>
        {
            _viewModel.TraceProgressChanged -= OnTraceProgressChanged;
            _viewModel.TraceCompleted -= OnTraceCompleted;
        };
        UpdateInputState();
        SetConfigurationState();
    }

    public bool ShowResultRequested { get; private set; }

    public bool ContinueTargetRequested { get; private set; }

    private void OnStopConditionChanged(object sender, RoutedEventArgs eventArgs) =>
        UpdateInputState();

    private async void OnStartClick(object sender, RoutedEventArgs eventArgs)
    {
        if (!TryBuildStopCondition(out TraceStopCondition condition))
        {
            StatusText.Visibility = Visibility.Visible;
            System.Windows.MessageBox.Show(
                this,
                StatusText.Text,
                "参数错误",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        TraceMode mode = StepIntoRadio.IsChecked == true
            ? TraceMode.StepInto
            : TraceMode.StepOver;
        SetTracingState();
        string? error = await _viewModel.StartTraceAsync(condition, mode);
        if (error is not null)
        {
            SetConfigurationState();
            StatusText.Text = error;
            StatusText.Visibility = Visibility.Visible;
            System.Windows.MessageBox.Show(
                this,
                error,
                "无法开始追踪",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }
    }

    private async void OnStopClick(object sender, RoutedEventArgs eventArgs)
    {
        if (!_isTracing)
        {
            return;
        }

        StopButton.IsEnabled = false;
        StatusText.Text = "正在停止追踪...";
        await _viewModel.StopTraceAsync();
    }

    private async void OnViewResultClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (!_isCompleted)
        {
            return;
        }

        Wpf.Ui.Controls.MessageBox prompt = new()
        {
            Title = "追踪完成",
            Content = "追踪已完成。请选择查看结果时目标程序的状态。",
            PrimaryButtonText = "继续运行",
            SecondaryButtonText = "保持中断",
            CloseButtonText = string.Empty,
            IsCloseButtonEnabled = false,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        Wpf.Ui.Controls.MessageBoxResult result = await prompt.ShowDialogAsync();
        ContinueTargetRequested =
            result == Wpf.Ui.Controls.MessageBoxResult.Primary;
        ShowResultRequested = true;
        DialogResult = true;
    }

    private void OnTraceProgressChanged(int current, int total)
    {
        TraceProgressPanel.Visibility = Visibility.Visible;
        TraceProgressBar.Value = total > 0
            ? Math.Clamp((double)current / total, 0, 1)
            : 0;
        ProgressText.Text = total > 0
            ? $"步数: {current:N0} / {total:N0}"
            : $"步数: {current:N0}";
    }

    private void OnTraceCompleted(TraceSession session)
    {
        _isTracing = false;
        _isCompleted = true;
        StopConditionHeader.Visibility = Visibility.Collapsed;
        StopConditionPanel.Visibility = Visibility.Collapsed;
        StepModeHeader.Visibility = Visibility.Collapsed;
        StepModePanel.Visibility = Visibility.Collapsed;
        TraceProgressPanel.Visibility = Visibility.Visible;
        StatusText.Visibility = Visibility.Visible;
        StartButton.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Collapsed;
        ViewResultButton.Visibility = Visibility.Visible;
        ViewResultButton.IsEnabled = true;
        CloseButton.IsEnabled = true;
        TraceProgressBar.Value = 1;
        ProgressText.Text = $"共 {session.Steps.Count:N0} 步 | {session.StopReason}";
        StatusText.Text = "追踪完成";
        Title = "追踪完成";
    }

    private void SetConfigurationState()
    {
        _isTracing = false;
        StopConditionHeader.Visibility = Visibility.Visible;
        StopConditionPanel.Visibility = Visibility.Visible;
        StepModeHeader.Visibility = Visibility.Visible;
        StepModePanel.Visibility = Visibility.Visible;
        TraceProgressPanel.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
        StatusText.Text = string.Empty;
        StartButton.Visibility = Visibility.Visible;
        StartButton.IsEnabled = true;
        StopButton.Visibility = Visibility.Collapsed;
        StopButton.IsEnabled = true;
        ViewResultButton.Visibility = Visibility.Collapsed;
        ViewResultButton.IsEnabled = false;
        CloseButton.IsEnabled = true;
        TraceProgressBar.Value = 0;
        ProgressText.Text = string.Empty;
        Title = "追踪配置";
    }

    private void SetTracingState()
    {
        _isTracing = true;
        _isCompleted = false;
        StopConditionHeader.Visibility = Visibility.Collapsed;
        StopConditionPanel.Visibility = Visibility.Collapsed;
        StepModeHeader.Visibility = Visibility.Collapsed;
        StepModePanel.Visibility = Visibility.Collapsed;
        TraceProgressPanel.Visibility = Visibility.Visible;
        StatusText.Visibility = Visibility.Visible;
        StartButton.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Visible;
        StopButton.IsEnabled = true;
        ViewResultButton.Visibility = Visibility.Collapsed;
        ViewResultButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        TraceProgressBar.Value = 0;
        ProgressText.Text = "步数: 0";
        StatusText.Text = "追踪中...";
        Title = "追踪进行中";
    }

    private bool TryBuildStopCondition(out TraceStopCondition condition)
    {
        condition = new TraceStopCondition();
        if (StepCountRadio.IsChecked == true)
        {
            if (!int.TryParse(
                    StepCountInput.Text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int maxSteps) ||
                maxSteps is < 1 or > 10_000_000)
            {
                StatusText.Text = "请输入 1~10000000 之间的步数。";
                return false;
            }

            condition.Type = TraceStopType.StepCount;
            condition.MaxSteps = maxSteps;
            return true;
        }

        if (RipMatchRadio.IsChecked == true)
        {
            if (!TryParseHex(RipInput.Text, out ulong targetRip) || targetRip == 0)
            {
                StatusText.Text = "请输入有效的目标 RIP。";
                return false;
            }

            condition.Type = TraceStopType.RipMatch;
            condition.TargetRip = targetRip;
            return true;
        }

        if (string.IsNullOrWhiteSpace(LuaConditionInput.Text))
        {
            StatusText.Text = "请输入 Lua 条件表达式。";
            return false;
        }

        condition.Type = TraceStopType.LuaCondition;
        condition.LuaCondition = LuaConditionInput.Text.Trim();
        return true;
    }

    private void UpdateInputState()
    {
        if (StepCountInput is null ||
            RipInput is null ||
            LuaConditionInput is null)
        {
            return;
        }

        StepCountInput.IsEnabled = StepCountRadio.IsChecked == true;
        RipInput.IsEnabled = RipMatchRadio.IsChecked == true;
        LuaConditionInput.IsEnabled = LuaConditionRadio.IsChecked == true;
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

    private async void OnCloseClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (_isTracing)
        {
            System.Windows.MessageBoxResult result = System.Windows.MessageBox.Show(
                this,
                "追踪正在进行中，确定要取消吗？",
                "确认",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            if (result != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }

            await _viewModel.StopTraceAsync();
        }

        ShowResultRequested = _isCompleted;
        DialogResult = _isCompleted;
    }
}
