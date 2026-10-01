using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DogeDebugger.Core.Disassembly;
using DogeDebugger.Core.Modules;
using DogeDebugger.Debugger.Session;

namespace DogeDebugger.UI.ViewModels.Panels;

public partial class InstructionSearchViewModel : ObservableObject
{
    private readonly DebuggerSession _session;
    private readonly Action<ulong>? _navigateToDisassembly;
    private readonly CommandSearchService _search = new();
    private CancellationTokenSource? _cancellation;
    private IReadOnlyList<CommandSearchResultItem> _allResults = [];

    public InstructionSearchViewModel(
        DebuggerSession session,
        Action<ulong>? navigateToDisassembly = null)
    {
        _session = session;
        _navigateToDisassembly = navigateToDisassembly;
    }

    [ObservableProperty]
    private string _instructionText = string.Empty;

    [ObservableProperty]
    private string _queryLabelText = "指令:";

    [ObservableProperty]
    private string _patternLabelText = "字节:";

    [ObservableProperty]
    private string _instructionBytesText = string.Empty;

    [ObservableProperty]
    private string _modulesText = string.Empty;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isSearching;

    [ObservableProperty]
    private CommandSearchResultItem? _selectedResult;

    [ObservableProperty]
    private ObservableCollection<CommandSearchResultItem> _filteredResults = [];

    public IReadOnlyList<CommandSearchResultItem> Results => _allResults;

    public bool CanStopSearch => IsSearching;

    public void Clear()
    {
        _allResults = [];
        FilteredResults = [];
        InstructionText = string.Empty;
        QueryLabelText = "指令:";
        PatternLabelText = "字节:";
        InstructionBytesText = string.Empty;
        ModulesText = string.Empty;
        SummaryText = string.Empty;
        FilterText = string.Empty;
        SelectedResult = null;
    }

    public async Task RunAsync(
        CommandSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _cancellation?.Dispose();
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        IsSearching = true;
        QueryLabelText = "指令:";
        PatternLabelText = "字节:";
        InstructionText = request.AssemblyText.Trim();
        InstructionBytesText = string.Empty;
        ModulesText = FormatModulesText(request.ScanModules);
        SummaryText = "正在扫描...";

        Progress<(double Progress, string Message)> progress = new(value =>
        {
            SummaryText = value.Message;
        });
        try
        {
            CommandSearchScanResult result = await Task.Run(
                    () => _search.Search(
                        _session.Target,
                        request,
                        progress,
                        _cancellation.Token),
                    _cancellation.Token)
                .ConfigureAwait(true);
            ApplyScanResult(result);
        }
        catch (OperationCanceledException)
        {
            SummaryText = "搜索已停止。";
        }
        catch (Exception exception)
        {
            SummaryText = $"搜索失败: {exception.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    public async Task RunReferenceSearchAsync(
        InstructionReferenceSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _cancellation?.Dispose();
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        IsSearching = true;
        QueryLabelText = "引用:";
        PatternLabelText = "匹配:";
        InstructionText = request.Kind == InstructionReferenceKind.Address
            ? $"地址 0x{request.Value:X16}"
            : $"常量 0x{request.Value:X}";
        InstructionBytesText =
            request.Kind == InstructionReferenceKind.Address
                ? "地址操作数 / 等值立即数"
                : "立即数 / 内存位移";
        ModulesText = FormatModulesText(request.ScanModules);
        SummaryText = "正在搜索...";

        Progress<(double Progress, string Message)> progress = new(value =>
        {
            SummaryText = value.Message;
        });
        try
        {
            CommandSearchScanResult result = await Task.Run(
                    () => _search.SearchReferences(
                        _session.Target,
                        request,
                        progress,
                        _cancellation.Token),
                    _cancellation.Token)
                .ConfigureAwait(true);
            ApplyScanResult(result);
        }
        catch (OperationCanceledException)
        {
            SummaryText = $"已取消，保留 {_allResults.Count:N0} 条结果";
        }
        catch (Exception exception)
        {
            SummaryText = $"搜索失败: {exception.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopSearch))]
    private void Stop() => _cancellation?.Cancel();

    public void NavigateToResult(CommandSearchResultItem? item)
    {
        if (item is not null)
        {
            _navigateToDisassembly?.Invoke(item.Address);
        }
    }

    partial void OnFilterTextChanged(string value) =>
        RebuildFilteredResults();

    private void RebuildFilteredResults()
    {
        if (_allResults.Count == 0)
        {
            FilteredResults = [];
            return;
        }

        string filter = FilterText.Trim();
        if (filter.Length == 0)
        {
            FilteredResults = new ObservableCollection<CommandSearchResultItem>(
                _allResults);
            return;
        }

        FilteredResults = new ObservableCollection<CommandSearchResultItem>(
            _allResults.Where(item =>
                item.AddressText.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.ModuleName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.ModuleOffsetText.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.BytesText.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.Disassembly.Contains(filter, StringComparison.OrdinalIgnoreCase)));
    }

    private void ApplyScanResult(CommandSearchScanResult result)
    {
        _allResults = result.Results;
        if (result.PatternBytes.Length > 0)
        {
            string bytesText = string.Join(
                ' ',
                result.PatternBytes.Select(static value => value.ToString("X2")));
            InstructionBytesText =
                result.MatchMode == CommandSearchMatchMode.Semantic
                    ? $"语义匹配，参考字节: {bytesText}"
                    : bytesText;
        }

        SummaryText = result.Truncated
            ? $"找到 {result.Results.Count:N0} 条结果，已截断，扫描 {result.ScannedBytes / 1024d / 1024d:N1} MB，用时 {result.Elapsed.TotalSeconds:N2}s"
            : $"找到 {result.Results.Count:N0} 条结果，扫描 {result.ScannedBytes / 1024d / 1024d:N1} MB，用时 {result.Elapsed.TotalSeconds:N2}s";
        RebuildFilteredResults();
    }

    private static string FormatModulesText(
        IReadOnlyList<ModuleDescriptor> modules)
    {
        if (modules.Count == 0)
        {
            return "未选择模块";
        }

        return modules.Count == 1
            ? modules[0].Name
            : $"{modules[0].Name} 等 {modules.Count} 个模块";
    }
}
