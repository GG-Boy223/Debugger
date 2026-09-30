using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DogeDebugger.Core.Process;
using DogeDebugger.Core.Settings;
using DogeDebugger.Core.Symbols;
using DogeDebugger.Debugger.Session;

namespace DogeDebugger.UI.ViewModels.Panels;

public partial class SourceDebugPanelViewModel : ObservableObject, IDisposable
{
    private const int MaxSourceBytes = 8 * 1024 * 1024;

    private readonly DebuggerSession _session;
    private readonly SettingsStore _settings;
    private NativeSymbolService _symbols = new();
    private NativeSymbolLocation? _pendingLocation;
    private bool _allowUnverifiedSourceOnce;
    private bool _disposed;

    public SourceDebugPanelViewModel(
        DebuggerSession session,
        SettingsStore settings)
    {
        _session = session;
        _settings = settings;
        Documents.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDocuments));
        Parameters.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(VariableCount));
            OnPropertyChanged(nameof(HasParameters));
        };
    }

    public ObservableCollection<SourceDocument> Documents { get; } = [];

    public ObservableCollection<ParameterNode> Parameters { get; } = [];

    public bool HasDocuments => Documents.Count > 0;

    public bool HasSelectedDocument => SelectedDocument is not null;

    public int VariableCount => Parameters.Count(node => !node.IsSeparator);

    public bool HasParameters => VariableCount > 0;

    public bool HasNavigationWarning =>
        !string.IsNullOrWhiteSpace(NavigationWarningText);

    public bool HasSourceContextBar =>
        HasSelectedDocument || HasNavigationWarning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedDocument))]
    [NotifyPropertyChangedFor(nameof(HasSourceContextBar))]
    private SourceDocument? _selectedDocument;

    [ObservableProperty]
    private string _parameterStatusText =
        "暂停时显示当前 RIP 仍有可靠 PDB 位置的变量";

    [ObservableProperty]
    private string _panelStatusText = "等待调试器暂停";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNavigationWarning))]
    [NotifyPropertyChangedFor(nameof(HasSourceContextBar))]
    private string _navigationWarningText = string.Empty;

    [ObservableProperty]
    private bool _canOpenUnverifiedSource;

    [ObservableProperty]
    private string _currentModuleText = string.Empty;

    [ObservableProperty]
    private string _currentFileText = string.Empty;

    [ObservableProperty]
    private string _currentLocationText = string.Empty;

    [ObservableProperty]
    private bool _isApproximate;

    public void OnTargetChanged()
    {
        _symbols.Dispose();
        _symbols = new NativeSymbolService();
        _pendingLocation = null;
        _allowUnverifiedSourceOnce = false;
        Documents.Clear();
        Parameters.Clear();
        SelectedDocument = null;
        CurrentModuleText = string.Empty;
        CurrentFileText = string.Empty;
        CurrentLocationText = string.Empty;
        NavigationWarningText = string.Empty;
        CanOpenUnverifiedSource = false;
        PanelStatusText = _session.Target.IsOpen
            ? "目标已打开，正在准备源码索引"
            : "等待打开目标进程";
        ParameterStatusText = "暂停时显示当前 RIP 仍有可靠 PDB 位置的变量";
    }

    public void OnPaused(ulong instructionPointer, uint threadId)
    {
        if (!_session.Target.IsOpen)
        {
            return;
        }

        PanelStatusText = $"已暂停，线程 {threadId}";
        if (!_symbols.LoadedModules.Any())
        {
            _ = RefreshIndexAsync();
        }

        LocateAddress(instructionPointer, approximate: false);
    }

    [RelayCommand]
    private async Task RefreshIndexAsync()
    {
        if (!_session.Target.IsOpen)
        {
            PanelStatusText = "请先打开目标进程。";
            return;
        }

        PanelStatusText = "正在刷新符号索引...";
        try
        {
            string sourceRoot = _settings.Current.SourceDebugging.SourceRoot;
            string pdbRoot = _settings.Current.SourceDebugging.PdbRoot;
            SymbolRefreshResult result = await Task.Run(() =>
                _symbols.Refresh(
                    _session.Target,
                    _session.EnumerateModules(),
                    pdbRoot,
                    sourceRoot))
                .ConfigureAwait(true);
            PanelStatusText =
                $"符号索引已刷新：加载 {result.LoadedModuleCount} 个模块" +
                (result.FailedModuleCount > 0
                    ? $"，失败 {result.FailedModuleCount} 个"
                    : string.Empty);
        }
        catch (Exception exception)
        {
            PanelStatusText = $"刷新符号索引失败：{exception.Message}";
        }
    }

    [RelayCommand]
    private void LocateCurrent()
    {
        if (!_session.Debugger.IsPaused)
        {
            PanelStatusText = "调试器未暂停，无法定位当前地址。";
            return;
        }

        LocateAddress(
            _session.Debugger.CurrentInstructionPointer,
            approximate: false);
    }

    [RelayCommand]
    private void OpenUnverifiedSource()
    {
        if (_pendingLocation is null)
        {
            return;
        }

        _allowUnverifiedSourceOnce = true;
        SourceDocument? document = OpenLocation(_pendingLocation);
        _allowUnverifiedSourceOnce = false;
        if (document is not null)
        {
            NavigationWarningText = string.Empty;
            CanOpenUnverifiedSource = false;
            _pendingLocation = null;
            PanelStatusText = "已按用户选择打开未验证的只读源码，未标记执行行。";
        }
    }

    [RelayCommand]
    private void CloseDocument(SourceDocument? document)
    {
        if (document is null)
        {
            return;
        }

        Documents.Remove(document);
        if (ReferenceEquals(SelectedDocument, document))
        {
            SelectedDocument = Documents.LastOrDefault();
        }
    }

    [RelayCommand]
    private void CloseOthers(SourceDocument? document)
    {
        if (document is null)
        {
            return;
        }

        foreach (SourceDocument candidate in Documents
                     .Where(candidate => !ReferenceEquals(candidate, document))
                     .ToArray())
        {
            Documents.Remove(candidate);
        }

        SelectedDocument = document;
    }

    public void SetParameters(
        IEnumerable<ParameterNode>? parameters,
        string? statusText = null)
    {
        Parameters.Clear();
        foreach (ParameterNode node in parameters ?? [])
        {
            Parameters.Add(node);
        }

        ParameterStatusText = statusText ??
            (VariableCount == 0
                ? "当前没有可用变量。"
                : $"{VariableCount} 个变量");
    }

    public void ClearExecution()
    {
        foreach (SourceDocument document in Documents)
        {
            document.ClearExecution();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _symbols.Dispose();
        _disposed = true;
    }

    private void LocateAddress(ulong address, bool approximate)
    {
        try
        {
            bool found = approximate
                ? _symbols.TryGetApproximateLine(address, out NativeSymbolLocation? location)
                : _symbols.TryGetLine(address, out location);
            if (!found && !approximate)
            {
                found = _symbols.TryGetApproximateLine(
                    address,
                    out location);
            }

            if (!found || location is null)
            {
                PanelStatusText =
                    $"未找到地址 0x{address:X} 对应的已加载 PDB 源码位置。";
                return;
            }

            ApplyLocation(location);
        }
        catch (Exception exception)
        {
            PanelStatusText = $"源码定位失败：{exception.Message}";
        }
    }

    private void ApplyLocation(NativeSymbolLocation location)
    {
        CurrentModuleText = string.IsNullOrWhiteSpace(location.ModuleName)
            ? "未知模块"
            : location.ModuleName;
        CurrentFileText = location.FilePath;
        CurrentLocationText = location.Line > 0
            ? $":{location.Line}"
            : location.IsApproximate
                ? "近似"
                : string.Empty;
        IsApproximate = location.IsApproximate;
        Parameters.Clear();
        Parameters.Add(new ParameterNode(
            "RIP",
            "uint64",
            $"0x{location.Address:X}",
            isLocalGroup: true));
        if (!string.IsNullOrWhiteSpace(location.FunctionName))
        {
            Parameters.Add(new ParameterNode(
                "函数",
                "symbol",
                location.FunctionName));
        }

        ParameterStatusText = VariableCount == 0
            ? "当前没有可用变量。"
            : $"{VariableCount} 个变量";

        if (string.IsNullOrWhiteSpace(location.FilePath) ||
            !File.Exists(location.FilePath))
        {
            PanelStatusText = "PDB 已返回源码位置，但源码文件不存在。";
            return;
        }

        SourceDocument? document = OpenLocation(location);
        if (document is not null)
        {
            PanelStatusText = location.IsApproximate
                ? "已近似定位到函数的首个语句行"
                : "已定位到源码";
        }
    }

    private SourceDocument? OpenLocation(NativeSymbolLocation location)
    {
        string filePath;
        try
        {
            filePath = Path.GetFullPath(location.FilePath);
        }
        catch (Exception exception)
        {
            PanelStatusText = $"无法打开源码：{exception.Message}";
            return null;
        }

        if (!IsSourcePathAllowed(filePath) && !_allowUnverifiedSourceOnce)
        {
            _pendingLocation = location;
            CanOpenUnverifiedSource = true;
            NavigationWarningText =
                "PDB 记录的源码不在当前源码根目录内，已阻止自动定位。" +
                "可以确认后用只读方式打开。";
            PanelStatusText = NavigationWarningText;
            return null;
        }

        try
        {
            FileInfo info = new(filePath);
            if (info.Length > MaxSourceBytes)
            {
                PanelStatusText = "源码文件过大，已阻止自动打开。";
                return null;
            }

            string content = File.ReadAllText(filePath);
            SourceDocument document = Documents.FirstOrDefault(
                candidate => string.Equals(
                    candidate.FilePath,
                    filePath,
                    StringComparison.OrdinalIgnoreCase)) ??
                AddDocument(filePath, content);
            foreach (SourceDocument candidate in Documents
                         .Where(candidate => !ReferenceEquals(candidate, document)))
            {
                candidate.ClearExecution();
            }

            if (_allowUnverifiedSourceOnce)
            {
                document.ApplyMarker(
                    0,
                    1,
                    SourceLineMarkerKind.None);
            }
            else
            {
                document.ApplyMarker(
                    location.Line,
                    1,
                    SourceLineMarkerKind.Current);
            }

            SelectedDocument = document;
            return document;
        }
        catch (Exception exception)
        {
            PanelStatusText = $"无法打开源码：{exception.Message}";
            return null;
        }
    }

    private SourceDocument AddDocument(string filePath, string content)
    {
        SourceDocument document = new(filePath, content);
        Documents.Add(document);
        return document;
    }

    private bool IsSourcePathAllowed(string filePath)
    {
        string root = _settings.Current.SourceDebugging.SourceRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            return true;
        }

        try
        {
            string rootPath = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            return filePath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
