using System.IO;
using System.Windows;
using DogeDebugger.Core.Modules;
using DogeDebugger.Core.Native;
using DogeDebugger.Core.Process;
using Microsoft.Win32;

namespace DogeDebugger.UI.Views.Dialogs;

public partial class DllInjectionWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly PeModuleAnalyzer _moduleAnalyzer = new();
    private readonly DllInjectionService _injectionService = new();
    private readonly bool _dhsLoaded = DogeHyperSecurityBridge.IsLoaded();
    private IReadOnlyList<PeExport> _exports = [];
    private PeExport? _selectedExport;
    private DllInjectionTarget? _target;
    private string _dllPath = string.Empty;
    private bool? _dllIs64Bit;
    private bool _isBusy;

    public DllInjectionWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        if (viewModel.IsTargetOpen)
        {
            _target = new DllInjectionTarget(
                viewModel.TargetProcessId,
                viewModel.TargetProcessName,
                viewModel.TargetProcessPath,
                viewModel.IsTarget64Bit);
        }

        ModeLoadLibraryRadio.Checked += OnInjectionModeChanged;
        ModeDhsRadio.Checked += OnInjectionModeChanged;
        Loaded += OnWindowLoaded;
        RefreshTargetState();
        UpdateButtonState();
    }

    private void OnSelectTargetClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        SelectTarget();
    }

    private async void OnReselectTargetClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        await ReselectTargetAsync();
    }

    private void SelectTarget()
    {
        ProcessDescriptor? previousSelection = _viewModel.SelectedProcess;
        ProcessSelectionWindow dialog = new(
            _viewModel,
            ProcessSelectionMode.Inject)
        {
            Owner = this
        };
        bool? result;
        ProcessDescriptor? selectedProcess;
        try
        {
            result = dialog.ShowDialog();
            selectedProcess = dialog.SelectedProcess;
        }
        finally
        {
            _viewModel.SelectedProcess = previousSelection;
        }

        if (result != true || selectedProcess is null)
        {
            return;
        }

        SetTarget(selectedProcess);
    }

    private void SetTarget(ProcessDescriptor process)
    {
        if (!_injectionService.TryCaptureTarget(
                process,
                out DllInjectionTarget target,
                out string error))
        {
            StatusText.Text = error;
            return;
        }

        _target = target;
        RefreshTargetState();
        UpdateButtonState();
        StatusText.Text = $"已选择目标进程 PID {target.ProcessId}。";
    }

    private async Task ReselectTargetAsync()
    {
        if (_target is null ||
            string.IsNullOrWhiteSpace(_target.FilePath))
        {
            StatusText.Text = "没有可用于重选的进程路径。";
            return;
        }

        string targetPath = _target.FilePath;
        SetBusy(true, "正在查找相同路径的目标进程...");
        try
        {
            ProcessDescriptor[] matches = await Task.Run(
                () => new ProcessCatalog()
                    .Enumerate()
                    .Where(process =>
                        string.Equals(
                            process.FilePath,
                            targetPath,
                            StringComparison.OrdinalIgnoreCase))
                    .ToArray());
            if (matches.Length == 0)
            {
                StatusText.Text = "未找到相同路径的目标进程，请手动选择。";
                return;
            }

            if (matches.Length > 1)
            {
                StatusText.Text = "找到多个相同路径的进程，请手动选择目标。";
                return;
            }

            SetTarget(matches[0]);
            StatusText.Text = $"已重选目标进程 PID {matches[0].ProcessId}。";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RefreshTargetState()
    {
        DhsBadgeText.Text = _dhsLoaded ? "DHS 已加载" : "DHS 未加载";
        if (_target is { } target)
        {
            TargetText.Text =
                $"{target.ProcessName} | PID: {target.ProcessId} | " +
                $"{(target.Is64Bit ? "x64" : "x86")}";
            TargetPathText.Text = string.IsNullOrWhiteSpace(target.FilePath)
                ? "无法读取进程路径"
                : target.FilePath;
            ReselectProcessButton.IsEnabled =
                !_isBusy &&
                !string.IsNullOrWhiteSpace(target.FilePath);
        }
        else
        {
            TargetText.Text = "未选择目标进程";
            TargetPathText.Text = "从当前进程自动填充，或点击“选择”指定其他目标。";
            ReselectProcessButton.IsEnabled = false;
        }

        bool dhsAvailable = _dhsLoaded && _target?.Is64Bit == true;
        ModeDhsRadio.IsEnabled = dhsAvailable;
        DhsModeHintText.Text = GetDhsModeHint();
        if (!dhsAvailable && ModeDhsRadio.IsChecked == true)
        {
            ModeLoadLibraryRadio.IsChecked = true;
        }
    }

    private void OnBrowseDllClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        OpenFileDialog dialog = new()
        {
            Title = "选择要注入的 DLL",
            Filter = "DLL 文件 (*.dll)|*.dll|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        LoadDll(dialog.FileName);
    }

    private void LoadDll(string path)
    {
        try
        {
            PeModuleMetadata metadata = _moduleAnalyzer.AnalyzeFile(path);
            if (!metadata.IsDll)
            {
                throw new InvalidDataException("所选文件不是 DLL。");
            }

            _dllPath = path;
            _dllIs64Bit = metadata.Is64Bit;
            _exports = metadata.Exports
                .Where(static export => !string.IsNullOrWhiteSpace(export.Name))
                .OrderBy(static export => export.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _selectedExport = null;
            DllPathBox.Text = path;
            DllArchText.Text = metadata.Is64Bit ? "x64" : "x86";
            ExportBox.Text = "不调用导出 / DllMain";
            StatusText.Text = _exports.Count == 0
                ? "DLL 已加载，未发现命名导出函数。"
                : $"DLL 已加载，发现 {_exports.Count} 个命名导出函数。";
        }
        catch (Exception exception)
        {
            _dllPath = string.Empty;
            _dllIs64Bit = null;
            _exports = [];
            _selectedExport = null;
            DllPathBox.Text = string.Empty;
            DllArchText.Text = string.Empty;
            ExportBox.Text = "不调用导出 / DllMain";
            StatusText.Text = exception.Message == "所选文件不是 DLL。"
                ? exception.Message
                : $"无法解析 DLL: {exception.Message}";
        }

        UpdateButtonState();
    }

    private void OnSelectExportClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (_exports.Count == 0)
        {
            StatusText.Text = "当前 DLL 没有可选择的命名导出函数。";
            return;
        }

        ExportSelectionWindow dialog = new(_exports, _selectedExport)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _selectedExport = dialog.SelectedExport;
        ExportBox.Text = _selectedExport is null
            ? "不调用导出 / DllMain"
            : $"{_selectedExport.Name} ({_selectedExport.Ordinal}, Ord #{_selectedExport.Ordinal})";
        StatusText.Text = _selectedExport is null
            ? "未选择导出函数。"
            : $"已选择导出函数 {_selectedExport.Name}。";
        UpdateButtonState();
    }

    private void OnClearExportClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        _selectedExport = null;
        ExportBox.Text = "不调用导出 / DllMain";
        StatusText.Text = "已清除导出函数选择，注入时仅调用 DllMain。";
        UpdateButtonState();
    }

    private async void OnInjectClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (!ValidateInjectionInput())
        {
            return;
        }

        SetBusy(true, "正在执行注入...");
        try
        {
            DllInjectionTarget target = _target!;
            PeExport? selectedExport = _selectedExport;
            DllInjectionMode mode = ModeDhsRadio.IsChecked == true
                ? DllInjectionMode.DogeHyperSecurity
                : DllInjectionMode.LoadLibrary;
            RemoteOperationResult result = await Task.Run(
                () => _injectionService.Inject(
                    target,
                    _dllPath,
                    selectedExport,
                    mode));
            if (!result.Succeeded)
            {
                StatusText.Text = result.ErrorMessage;
                return;
            }

            StatusText.Text = mode == DllInjectionMode.DogeHyperSecurity
                ? $"DHS 注入请求已完成。结果: 0x{result.ReturnValue:X}。 ({FormatElapsed(result.Elapsed)})"
                : string.IsNullOrWhiteSpace(_selectedExport?.Name)
                    ? $"DLL 已注入。 ({FormatElapsed(result.Elapsed)})"
                    : $"DLL 已注入，并调用导出函数 {_selectedExport.Name}。 ({FormatElapsed(result.Elapsed)})";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"注入失败：{exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnUnloadClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (_target is null || string.IsNullOrWhiteSpace(_dllPath))
        {
            return;
        }

        SetBusy(true, "正在卸载模块...");
        try
        {
            DllInjectionTarget target = _target;
            RemoteOperationResult result = await Task.Run(
                () => _injectionService.Unload(
                    target,
                    _dllPath));
            if (result.Succeeded)
            {
                StatusText.Text = $"DLL 已卸载。 ({FormatElapsed(result.Elapsed)})";
            }
            else
            {
                StatusText.Text = result.ErrorMessage;
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = $"卸载失败：{exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private bool ValidateInjectionInput()
    {
        if (_target is null)
        {
            StatusText.Text = "请先选择目标进程。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_dllPath) || !File.Exists(_dllPath))
        {
            StatusText.Text = "请先选择 DLL 文件。";
            return false;
        }

        if (!_dllIs64Bit.HasValue)
        {
            StatusText.Text = "DLL 架构未知，请重新选择 DLL。";
            return false;
        }

        if (_target.Is64Bit != _dllIs64Bit.Value)
        {
            StatusText.Text =
                $"架构不匹配：目标是 {(_target.Is64Bit ? "x64" : "x86")}，" +
                $"DLL 是 {(_dllIs64Bit.Value ? "x64" : "x86")}。";
            return false;
        }

        return true;
    }

    private void SetBusy(bool isBusy, string? status = null)
    {
        _isBusy = isBusy;
        if (!string.IsNullOrWhiteSpace(status))
        {
            StatusText.Text = status;
        }

        BusyBar.Visibility = isBusy
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateButtonState();
    }

    private void UpdateButtonState()
    {
        bool hasDll =
            !string.IsNullOrWhiteSpace(_dllPath) &&
            File.Exists(_dllPath) &&
            _dllIs64Bit.HasValue;
        bool architectureMatches =
            _target is not null &&
            _dllIs64Bit is { } is64Bit &&
            _target.Is64Bit == is64Bit;
        bool modeAvailable =
            ModeLoadLibraryRadio.IsChecked == true ||
            (_dhsLoaded && _target?.Is64Bit == true);
        SelectExportButton.IsEnabled = !_isBusy && _exports.Count > 0;
        ClearExportButton.IsEnabled = !_isBusy && _selectedExport is not null;
        InjectButton.IsEnabled =
            !_isBusy &&
            _target is not null &&
            hasDll &&
            architectureMatches &&
            modeAvailable;
        UnloadButton.IsEnabled =
            !_isBusy &&
            _target is not null &&
            !string.IsNullOrWhiteSpace(_dllPath) &&
            File.Exists(_dllPath);
        ReselectProcessButton.IsEnabled =
            !_isBusy &&
            !string.IsNullOrWhiteSpace(_target?.FilePath);
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalMilliseconds < 1000
            ? $"{elapsed.TotalMilliseconds:0} ms"
            : $"{elapsed.TotalSeconds:0.00} s";

    private void OnCloseClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        Close();
    }

    private void OnInjectionModeChanged(
        object sender,
        RoutedEventArgs eventArgs)
    {
        UpdateButtonState();
    }

    private void OnWindowLoaded(
        object sender,
        RoutedEventArgs eventArgs)
    {
        RefreshTargetState();
        UpdateButtonState();
        if (string.IsNullOrWhiteSpace(_dllPath))
        {
            StatusText.Text = _target is null
                ? "选择目标进程和 DLL 后即可注入。"
                : "已使用当前进程作为目标，请选择 DLL。";
        }
    }

    private string GetDhsModeHint()
    {
        if (!_dhsLoaded)
        {
            return "DHS 未加载，当前不可用。";
        }

        if (_target is null)
        {
            return "选择 x64 目标进程后可用。";
        }

        return _target.Is64Bit
            ? "利用DHS辅助使用。"
            : "模式二仅支持 x64 目标进程。";
    }
}
