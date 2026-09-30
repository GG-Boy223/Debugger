using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using DogeDebugger.Core.Cache;
using DogeDebugger.Core.Modules;
using DogeDebugger.Core.Signatures.CrossVersion;
using Microsoft.Win32;

namespace DogeDebugger.UI.Views.Dialogs;

public partial class SignatureSearchWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly CrossVersionLocatorService _locator = new();
    private readonly CacheCrossVersionLocatorService _cacheLocator = new();
    private readonly ObservableCollection<SignatureSearchResultRow> _rows = [];
    private readonly ObservableCollection<SignatureCacheArtifactOption> _cacheOptions = [];
    private CancellationTokenSource? _cancellation;
    private LoadedPeImage? _aImage;
    private LoadedPeImage? _bImage;
    private LoadedPeImage? _aCacheImage;
    private LoadedPeImage? _bCacheImage;
    private StaticAnalysisCacheDocument? _aCacheDocument;
    private StaticAnalysisCacheDocument? _bCacheDocument;
    private ModuleDescriptor? _bCurrentModule;
    private int _runningRowCount;

    public SignatureSearchWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        TargetResultsGrid.ItemsSource = _rows;
        ACacheCombo.ItemsSource = _cacheOptions;
        BCacheCombo.ItemsSource = _cacheOptions;
        TargetKindCombo.ItemsSource = TargetKinds;
        TargetKindCombo.SelectedIndex = 0;
        ASourceExternalRadio.IsChecked = true;
        BSourceCurrentProcessRadio.IsChecked = true;
        RefreshSourcePanels();
        RefreshCacheOptions();
        RefreshLiveModules();
        RefreshSourceStatus();
    }

    public IReadOnlyList<SignatureTargetKindOption> TargetKinds { get; } =
    [
        new("自动识别", CrossVersionTargetKind.Auto),
        new("函数入口", CrossVersionTargetKind.Function),
        new("函数内部", CrossVersionTargetKind.FunctionInterior),
        new("全局数据", CrossVersionTargetKind.GlobalData),
        new("字符串", CrossVersionTargetKind.String),
        new("未知地址", CrossVersionTargetKind.Unknown)
    ];

    private SignatureTargetKindOption SelectedKind =>
        TargetKindCombo.SelectedItem as SignatureTargetKindOption ??
        TargetKinds[0];

    private void OnASourceModeChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!IsLoaded)
        {
            return;
        }

        RefreshSourcePanels();
        RefreshSourceStatus();
    }

    private void OnBSourceModeChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!IsLoaded)
        {
            return;
        }

        if (BSourceCurrentProcessRadio.IsChecked != true)
        {
            _bCurrentModule = null;
        }
        else
        {
            RefreshLiveModules();
        }

        RefreshSourcePanels();
        RefreshSourceStatus();
    }

    private void RefreshSourcePanels()
    {
        ASourceCachePanel.Visibility = ASourceCacheRadio.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        ASourceExternalPanel.Visibility = ASourceExternalRadio.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        BSourceCachePanel.Visibility = BSourceCacheRadio.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        BSourceExternalPanel.Visibility = BSourceExternalRadio.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        BSourceCurrentProcessPanel.Visibility =
            BSourceCurrentProcessRadio.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void RefreshSourceStatus()
    {
        if (ASourceCacheRadio.IsChecked == true)
        {
            StaticAnalysisCacheArtifact? artifact =
                (ACacheCombo.SelectedItem as SignatureCacheArtifactOption)?.Artifact;
            ASourceMetadataText.Text = artifact?.MetadataText ?? "请选择一组已配对缓存";
            ASourceStatusText.Text = artifact?.StatusText ?? "未选择缓存";
        }
        else
        {
            ASourceMetadataText.Text = _aImage is null
                ? "未加载 A 版映像"
                : $"{_aImage.Name} · {(_aImage.Is64Bit ? "x64" : "x86")} · " +
                  $"{_aImage.Metadata.Sections.Count} 个节";
            ASourceStatusText.Text = _aImage is null
                ? "请选择外部文件"
                : "A 版映像已就绪";
        }

        if (BSourceCacheRadio.IsChecked == true)
        {
            StaticAnalysisCacheArtifact? artifact =
                (BCacheCombo.SelectedItem as SignatureCacheArtifactOption)?.Artifact;
            BSourceMetadataText.Text = artifact?.MetadataText ?? "请选择一组已配对缓存";
            BSourceStatusText.Text = artifact?.StatusText ?? "未选择缓存";
        }
        else
        {
            if (BSourceCurrentProcessRadio.IsChecked == true)
            {
                ModuleDescriptor? module =
                    BModuleCombo.SelectedItem as ModuleDescriptor;
                BSourceMetadataText.Text = !_viewModel.Target.IsOpen
                    ? "当前进程未打开"
                    : module is not null
                        ? $"{module.Name} · 基址 0x{module.BaseAddress:X} · " +
                          $"{module.SizeText}"
                        : $"当前进程 {_viewModel.TargetProcessName} " +
                          $"PID {_viewModel.TargetProcessId}";
                BSourceStatusText.Text = !_viewModel.Target.IsOpen
                    ? "请先打开进程"
                    : module is not null
                        ? "B 版映像已就绪"
                        : "请选择目标模块";
            }
            else
            {
                BSourceMetadataText.Text = _bImage is null
                    ? "未加载 B 版映像"
                    : $"{_bImage.Name} · {(_bImage.Is64Bit ? "x64" : "x86")} · " +
                      $"{_bImage.Metadata.Sections.Count} 个节";
                BSourceStatusText.Text = _bImage is null
                    ? "请选择外部文件"
                    : "B 版映像已就绪";
            }
        }

        CurrentProcessText.Text = _viewModel.Target.IsOpen
            ? $"{_viewModel.TargetProcessName} · PID {_viewModel.TargetProcessId} · " +
              $"{(_viewModel.Target.Is64Bit ? "x64" : "x86")}"
            : "尚未打开进程";
    }

    private void OnACacheSelectionChanged(
        object sender,
        SelectionChangedEventArgs eventArgs)
    {
        _aCacheDocument = null;
        _aCacheImage = TryLoadCacheModuleImage(
            ACacheCombo.SelectedItem as SignatureCacheArtifactOption);
        RefreshSourceStatus();
    }

    private void OnBCacheSelectionChanged(
        object sender,
        SelectionChangedEventArgs eventArgs)
    {
        _bCacheDocument = null;
        _bCacheImage = TryLoadCacheModuleImage(
            BCacheCombo.SelectedItem as SignatureCacheArtifactOption);
        _bCurrentModule = null;
        RefreshSourceStatus();
    }

    private static LoadedPeImage? TryLoadCacheModuleImage(
        SignatureCacheArtifactOption? option)
    {
        if (option is null)
        {
            return null;
        }

        string? modulePath = option.Artifact.ResolveModulePath();
        if (modulePath is not null)
        {
            try
            {
                return LoadedPeImage.FromFile(modulePath);
            }
            catch
            {
            }
        }

        string? dumpPath = option.Artifact.DumpPath;
        if (dumpPath is null)
        {
            return null;
        }

        try
        {
            return LoadedPeImage.FromFile(dumpPath);
        }
        catch
        {
            return null;
        }
    }

    private void OnBrowseASourceClick(object sender, RoutedEventArgs eventArgs)
    {
        _aImage = SelectImage("选择 A 版程序文件", AFilePathBox);
        RefreshSourceStatus();
    }

    private void OnBrowseBSourceClick(object sender, RoutedEventArgs eventArgs)
    {
        _bImage = SelectImage("选择 B 版程序文件", BFilePathBox);
        _bCurrentModule = null;
        RefreshSourceStatus();
    }

    private LoadedPeImage? SelectImage(string title, TextBox pathBox)
    {
        OpenFileDialog dialog = new()
        {
            Title = title,
            Filter = "程序文件 (*.exe;*.dll)|*.exe;*.dll|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return null;
        }

        try
        {
            LoadedPeImage image = LoadedPeImage.FromFile(dialog.FileName);
            pathBox.Text = dialog.FileName;
            return image;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "加载失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return null;
        }
    }

    private void OnRefreshCacheClick(object sender, RoutedEventArgs eventArgs)
    {
        RefreshCacheOptions();
    }

    private void RefreshCacheOptions()
    {
        string? previousAId =
            (ACacheCombo.SelectedItem as SignatureCacheArtifactOption)?.ArtifactId;
        string? previousBId =
            (BCacheCombo.SelectedItem as SignatureCacheArtifactOption)?.ArtifactId;
        _cacheOptions.Clear();
        _aCacheDocument = null;
        _bCacheDocument = null;
        StaticAnalysisCacheArtifact[] artifacts = StaticAnalysisCacheArtifactStore
            .EnumerateArtifacts()
            .OrderByDescending(static artifact => artifact.IsPaired)
            .ThenByDescending(static artifact => artifact.LastWriteTime)
            .ThenBy(
                static artifact => artifact.ModuleName,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (StaticAnalysisCacheArtifact artifact in artifacts)
        {
            _cacheOptions.Add(new SignatureCacheArtifactOption(
                artifact.DisplayName,
                artifact.ArtifactId,
                artifact));
        }

        ACacheCombo.SelectedItem = previousAId is null
            ? _cacheOptions.FirstOrDefault()
            : _cacheOptions.FirstOrDefault(option => option.ArtifactId == previousAId)
                ?? _cacheOptions.FirstOrDefault();
        BCacheCombo.SelectedItem = previousBId is null
            ? _cacheOptions.FirstOrDefault()
            : _cacheOptions.FirstOrDefault(option => option.ArtifactId == previousBId)
                ?? _cacheOptions.FirstOrDefault();
        if (artifacts.Length == 0)
        {
            BatchStatusText.Text = "当前没有可用的静态分析缓存对。";
        }

        _aCacheImage = TryLoadCacheModuleImage(
            ACacheCombo.SelectedItem as SignatureCacheArtifactOption);
        _bCacheImage = TryLoadCacheModuleImage(
            BCacheCombo.SelectedItem as SignatureCacheArtifactOption);
        RefreshSourceStatus();
    }

    private void OnRefreshModulesClick(object sender, RoutedEventArgs eventArgs)
    {
        RefreshLiveModules();
    }

    private void RefreshLiveModules()
    {
        if (!_viewModel.Target.IsOpen)
        {
            BModuleCombo.ItemsSource = Array.Empty<ModuleDescriptor>();
            BModuleCombo.SelectedItem = null;
            RefreshSourceStatus();
            return;
        }

        IReadOnlyList<ModuleDescriptor> modules = _viewModel.EnumerateTargetModules();
        BModuleCombo.ItemsSource = modules;
        BModuleCombo.SelectedItem =
            modules.FirstOrDefault(module => module.IsMainModule) ??
            modules.FirstOrDefault();
        _bCurrentModule = BModuleCombo.SelectedItem as ModuleDescriptor;
        RefreshSourceStatus();
    }

    private async void OnStartBatchClick(object sender, RoutedEventArgs eventArgs)
    {
        if (!TryParseTargets(BatchTargetsBox.Text, out List<CrossVersionBatchTarget> targets))
        {
            return;
        }

        bool useCacheDocuments =
            ASourceCacheRadio.IsChecked == true &&
            BSourceCacheRadio.IsChecked == true;
        StaticAnalysisCacheDocument? sourceDocument = null;
        StaticAnalysisCacheDocument? targetDocument = null;
        SignatureCacheArtifactOption? sourceCacheOption = null;
        SignatureCacheArtifactOption? targetCacheOption = null;
        CrossVersionTargetKind targetKind = SelectedKind.Kind;
        LoadedPeImage? sourceDumpImage = null;
        LoadedPeImage? targetDumpImage = null;
        LoadedPeImage sourceImage = null!;
        LoadedPeImage targetImage = null!;
        if (useCacheDocuments)
        {
            if (!TryGetCacheDocuments(
                    out sourceDocument,
                    out targetDocument,
                    out sourceCacheOption,
                    out targetCacheOption))
            {
                return;
            }

            sourceDumpImage = TryLoadCacheDumpImage(sourceCacheOption);
            targetDumpImage = TryLoadCacheDumpImage(targetCacheOption);
        }
        else if (!TryGetAImage(out sourceImage))
        {
            return;
        }
        else if (!TryLoadBImage(out targetImage))
        {
            return;
        }

        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        SetRunning(true);
        _rows.Clear();
        Progress<CrossVersionProgress> progress = new(item =>
        {
            BatchProgressBar.Value = item.Percentage;
            BatchStatusText.Text = item.Message;
        });
        try
        {
            IReadOnlyList<CrossVersionResult> results = useCacheDocuments
                ? await Task.Run(
                    () => _cacheLocator.Locate(
                        sourceDocument!,
                        targetDocument!,
                        targets,
                        targetKind,
                        progress,
                        _cancellation.Token,
                        sourceDumpImage,
                        targetDumpImage),
                    _cancellation.Token)
                : await _locator.LocateAsync(
                    sourceImage,
                    targetImage,
                    targets,
                    targetKind,
                    progress,
                    _cancellation.Token);
            if (!useCacheDocuments &&
                BSourceCurrentProcessRadio.IsChecked == true &&
                _bCurrentModule is not null &&
                _viewModel.Target.IsOpen)
            {
                results = results
                    .Select(result => LiveMemoryVerifier.Verify(
                        result,
                        sourceImage,
                        _viewModel.Target,
                        _bCurrentModule.BaseAddress))
                    .ToArray();
            }

            _runningRowCount = results.Count;
            for (int index = 0; index < results.Count; index++)
            {
                _rows.Add(new SignatureSearchResultRow(index + 1, results[index]));
            }

            int resolved = results.Count(result =>
                result.State == CrossVersionResolutionState.Resolved);
            int ambiguous = results.Count(result =>
                result.State == CrossVersionResolutionState.Ambiguous);
            ResultStatusText.Text =
                $"共 {results.Count} 个目标，已定位 {resolved} 个，存在歧义 {ambiguous} 个。";
            ExportResultsButton.IsEnabled = resolved > 0;
        }
        catch (OperationCanceledException)
        {
            BatchStatusText.Text = "批量定位已取消。";
        }
        catch (Exception exception)
        {
            BatchStatusText.Text = $"批量定位失败：{exception.Message}";
        }
        finally
        {
            SetRunning(false);
        }
    }

    private bool TryGetAImage(out LoadedPeImage image)
    {
        image = null!;
        if (ASourceCacheRadio.IsChecked == true)
        {
            if (ACacheCombo.SelectedItem is not SignatureCacheArtifactOption option)
            {
                BatchStatusText.Text = "请选择 A 版静态分析缓存对。";
                return false;
            }

            if (!option.Artifact.IsPaired)
            {
                BatchStatusText.Text = $"A 版缓存不完整：{option.Artifact.StatusText}。";
                return false;
            }

            if (!TryLoadCachePayload(option, "A", out _aCacheDocument))
            {
                return false;
            }

            if (_aCacheImage is null)
            {
                BatchStatusText.Text = "所选 A 版缓存缺少可用的模块文件。";
                return false;
            }

            image = _aCacheImage;
            return true;
        }

        if (_aImage is null)
        {
            BatchStatusText.Text = "请先加载 A 版程序文件。";
            return false;
        }

        image = _aImage;
        return true;
    }

    private bool TryLoadBImage(out LoadedPeImage image)
    {
        image = null!;
        try
        {
            if (BSourceCacheRadio.IsChecked == true)
            {
                if (BCacheCombo.SelectedItem is not SignatureCacheArtifactOption option)
                {
                    BatchStatusText.Text = "请选择 B 版静态分析缓存对。";
                    return false;
                }

                if (!option.Artifact.IsPaired)
                {
                    BatchStatusText.Text = $"B 版缓存不完整：{option.Artifact.StatusText}。";
                    return false;
                }

                if (!TryLoadCachePayload(option, "B", out _bCacheDocument))
                {
                    return false;
                }

                if (_bCacheImage is null)
                {
                    BatchStatusText.Text = "所选 B 版缓存缺少可用的模块文件。";
                    return false;
                }

                _bCurrentModule = null;
                image = _bCacheImage;
                return true;
            }

            if (BSourceCurrentProcessRadio.IsChecked == true)
            {
                if (_viewModel.Target is not { IsOpen: true })
                {
                    BatchStatusText.Text = "请先打开并附加目标进程。";
                    return false;
                }

                ModuleDescriptor? module = BModuleCombo.SelectedItem as ModuleDescriptor;
                if (module is null)
                {
                    BatchStatusText.Text = "请选择当前进程中的目标模块。";
                    return false;
                }

                _bCurrentModule = module;
                image = LoadedPeImage.FromProcessModule(_viewModel.Target, module);
                _bImage = image;
                RefreshSourceStatus();
                return true;
            }

            if (_bImage is null)
            {
                BatchStatusText.Text = "请先加载 B 版程序文件或选择当前进程模块。";
                return false;
            }

            image = _bImage;
            return true;
        }
        catch (Exception exception)
        {
            BatchStatusText.Text = exception.Message;
            return false;
        }
    }

    private bool TryGetCacheDocuments(
        out StaticAnalysisCacheDocument sourceDocument,
        out StaticAnalysisCacheDocument targetDocument,
        out SignatureCacheArtifactOption sourceOption,
        out SignatureCacheArtifactOption targetOption)
    {
        sourceDocument = null!;
        targetDocument = null!;
        sourceOption = null!;
        targetOption = null!;
        if (ACacheCombo.SelectedItem is not SignatureCacheArtifactOption aOption)
        {
            BatchStatusText.Text = "请选择 A 版静态分析缓存对。";
            return false;
        }

        if (BCacheCombo.SelectedItem is not SignatureCacheArtifactOption bOption)
        {
            BatchStatusText.Text = "请选择 B 版静态分析缓存对。";
            return false;
        }

        if (!aOption.Artifact.IsPaired)
        {
            BatchStatusText.Text =
                $"A 版缓存不完整：{aOption.Artifact.StatusText}。";
            return false;
        }

        if (!bOption.Artifact.IsPaired)
        {
            BatchStatusText.Text =
                $"B 版缓存不完整：{bOption.Artifact.StatusText}。";
            return false;
        }

        if (!TryLoadCachePayload(aOption, "A", out sourceDocument) ||
            !TryLoadCachePayload(bOption, "B", out targetDocument))
        {
            return false;
        }

        sourceOption = aOption;
        targetOption = bOption;
        return true;
    }

    private static LoadedPeImage? TryLoadCacheDumpImage(
        SignatureCacheArtifactOption? option)
    {
        if (option is null)
        {
            return null;
        }

        string? dumpPath = option.Artifact.DumpPath;
        if (dumpPath is null)
        {
            return null;
        }

        try
        {
            return LoadedPeImage.FromFile(dumpPath);
        }
        catch
        {
            return null;
        }
    }

    private bool TryLoadCachePayload(
        SignatureCacheArtifactOption option,
        string version,
        out StaticAnalysisCacheDocument document)
    {
        document = null!;
        string? analyzePath = option.Artifact.AnalyzePath;
        if (analyzePath is null)
        {
            BatchStatusText.Text = $"{version} 版缓存缺少静态分析文件。";
            return false;
        }

        try
        {
            document = StaticAnalysisCachePayloadReader.Read(analyzePath);
            return true;
        }
        catch (Exception exception)
        {
            BatchStatusText.Text =
                $"{version} 版缓存载荷无法读取：{exception.Message}";
            return false;
        }
    }

    private static bool TryParseTargets(
        string text,
        out List<CrossVersionBatchTarget> targets)
    {
        targets = [];
        string[] lines = text.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string line in lines)
        {
            if (line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            int separator = line.IndexOf('=');
            if (separator <= 0 || separator >= line.Length - 1)
            {
                continue;
            }

            string name = line[..separator].Trim();
            string offsetText = line[(separator + 1)..].Trim();
            if (offsetText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                offsetText = offsetText[2..];
            }

            if (name.Length == 0 ||
                !uint.TryParse(
                    offsetText,
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out uint offset))
            {
                continue;
            }

            targets.Add(new CrossVersionBatchTarget
            {
                Name = name,
                SourceOffset = offset
            });
        }

        return targets.Count > 0;
    }

    private void OnCancelTaskClick(object sender, RoutedEventArgs eventArgs)
    {
        _cancellation?.Cancel();
    }

    private void OnTargetResultsSelectionChanged(
        object sender,
        SelectionChangedEventArgs eventArgs)
    {
        UpdateActionState();
    }

    private void OnTargetResultsMouseDoubleClick(
        object sender,
        System.Windows.Input.MouseButtonEventArgs eventArgs)
    {
        if (TargetResultsGrid.SelectedItem is SignatureSearchResultRow row)
        {
            OpenDetail(row);
        }
    }

    private void OnTargetResultsPreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == System.Windows.Input.Key.Enter &&
            TargetResultsGrid.SelectedItem is SignatureSearchResultRow row)
        {
            OpenDetail(row);
            eventArgs.Handled = true;
        }
    }

    private void OpenDetail(SignatureSearchResultRow row)
    {
        SignatureSearchDetailWindow window = new(row.Result)
        {
            Owner = this
        };
        if (window.ShowDialog() == true && window.ConfirmedCandidate is { } candidate)
        {
            row.Confirm(candidate);
            TargetResultsGrid.Items.Refresh();
            UpdateActionState();
            ExportResultsButton.IsEnabled = _rows.Any(item =>
                item.Result.State == CrossVersionResolutionState.Resolved);
        }
    }

    private void OnExportResultsClick(object sender, RoutedEventArgs eventArgs)
    {
        SignatureSearchResultRow[] exportRows = _rows
            .Where(row => row.Result.State == CrossVersionResolutionState.Resolved &&
                          row.EffectiveOffset is not null)
            .ToArray();
        if (exportRows.Length == 0)
        {
            return;
        }

        SaveFileDialog dialog = new()
        {
            Title = "导出 B 版偏移",
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            DefaultExt = ".txt",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        StringBuilder builder = new();
        foreach (SignatureSearchResultRow row in exportRows)
        {
            builder.Append(row.Name)
                .Append('=')
                .Append(row.EffectiveOffset!.Value.ToString("X", CultureInfo.InvariantCulture))
                .AppendLine();
        }

        File.WriteAllText(
            dialog.FileName,
            builder.ToString(),
            new UTF8Encoding(false));
        ResultStatusText.Text = $"已导出 {exportRows.Length} 条 B 版偏移。";
    }

    private async void OnJumpDisassemblyClick(object sender, RoutedEventArgs eventArgs)
    {
        if (TargetResultsGrid.SelectedItem is not SignatureSearchResultRow row ||
            row.EffectiveOffset is not { } offset ||
            _bCurrentModule is null)
        {
            return;
        }

        await _viewModel.NavigateToAddressAsync(
            _bCurrentModule.BaseAddress + offset);
        ResultStatusText.Text = $"已跳转到 0x{_bCurrentModule.BaseAddress + offset:X}。";
    }

    private void OnCloseClick(object sender, RoutedEventArgs eventArgs)
    {
        _cancellation?.Cancel();
        Close();
    }

    private void SetRunning(bool running)
    {
        StartBatchButton.IsEnabled = !running;
        CancelTaskButton.IsEnabled = running;
        BatchProgressBar.IsIndeterminate = false;
        if (!running)
        {
            BatchProgressBar.Value = 0;
        }

        UpdateActionState();
    }

    private void UpdateActionState()
    {
        SignatureSearchResultRow? selected =
            TargetResultsGrid.SelectedItem as SignatureSearchResultRow;
        ExportResultsButton.IsEnabled =
            _rows.Any(row =>
                row.Result.State == CrossVersionResolutionState.Resolved &&
                row.EffectiveOffset is not null);
        JumpDisassemblyButton.IsEnabled =
            selected?.EffectiveOffset is not null &&
            _bCurrentModule is not null;
    }
}

public sealed record SignatureTargetKindOption(
    string DisplayName,
    CrossVersionTargetKind Kind);

public sealed record SignatureCacheArtifactOption(
    string DisplayName,
    string ArtifactId,
    StaticAnalysisCacheArtifact Artifact);

public sealed class SignatureSearchResultRow
{
    public SignatureSearchResultRow(int rowNumber, CrossVersionResult result)
    {
        RowNumber = rowNumber;
        Result = result;
        EffectiveOffset = result.ResolvedOffset;
    }

    public int RowNumber { get; }

    public string Name => Result.Name;

    public CrossVersionResult Result { get; private set; }

    public uint? EffectiveOffset { get; private set; }

    public bool IsManuallyConfirmed { get; private set; }

    public string SourceOffsetText => Result.SourceOffsetText;

    public string StateText => IsManuallyConfirmed
        ? "已确认"
        : Result.State switch
        {
            CrossVersionResolutionState.Resolved => "已定位",
            CrossVersionResolutionState.Ambiguous => "有歧义",
            CrossVersionResolutionState.Unresolved => "未解析",
            CrossVersionResolutionState.Failed => "失败",
            _ => "未知"
        };

    public string EffectiveOffsetText => EffectiveOffset is { } offset
        ? $"0x{offset:X}"
        : "—";

    public string ConfidenceText
    {
        get
        {
            CrossVersionCandidate? candidate = EffectiveCandidate;
            if (candidate is not null)
            {
                return candidate.Confidence switch
                {
                    >= 0.8 => "高",
                    >= 0.55 => "中",
                    _ => "低"
                };
            }

            return Result.State == CrossVersionResolutionState.Ambiguous
                ? "待确认"
                : "—";
        }
    }

    public string EvidenceCountText => Result.Evidence.Count == 0
        ? "—"
        : Result.Evidence.Count.ToString(CultureInfo.InvariantCulture);

    public int EvidenceCount => Result.Evidence.Count;

    public string Explanation => IsManuallyConfirmed
        ? "候选结果已由用户确认"
        : Result.State switch
        {
            CrossVersionResolutionState.Resolved => "多条独立证据已形成共识",
            CrossVersionResolutionState.Ambiguous => "存在多个候选，需要人工确认",
            CrossVersionResolutionState.Unresolved => "证据不足或相互冲突",
            _ => "尚无结果"
        };

    private CrossVersionCandidate? EffectiveCandidate =>
        EffectiveOffset is { } offset
            ? Result.Candidates.FirstOrDefault(candidate =>
                candidate.TargetOffset == offset)
            : Result.State == CrossVersionResolutionState.Resolved
                ? Result.Candidates.FirstOrDefault()
                : null;

    public void Confirm(CrossVersionCandidate candidate)
    {
        EffectiveOffset = candidate.TargetOffset;
        IsManuallyConfirmed = true;
        Result = new CrossVersionResult
        {
            Name = Result.Name,
            SourceOffset = Result.SourceOffset,
            State = CrossVersionResolutionState.Resolved,
            ResolvedOffset = candidate.TargetOffset,
            Confidence = candidate.Confidence,
            Explanation = "候选结果已由用户确认",
            Evidence = Result.Evidence,
            Candidates = Result.Candidates
        };
    }
}
