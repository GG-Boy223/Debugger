using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using DogeDebugger.Core.Modules;
using DogeDebugger.Core.Search;
using Microsoft.Win32;

namespace DogeDebugger.UI.Views.Dialogs;

public partial class PointerScanResultWindow : Window
{
    private const int PageSize = 10_000;

    private readonly MainViewModel _viewModel;
    private readonly ObservableCollection<PointerScanModuleFilterItem> _moduleFilters = [];
    private readonly List<int> _filteredIndices = [];
    private PointerScanOptions? _currentOptions;
    private PointerScanResult? _result;
    private CancellationTokenSource? _scanCancellation;
    private int _currentPage = 1;
    private int _totalPages = 1;
    private int _maxDepthDisplay;
    private bool _isScanning;
    private bool _usesModuleFilter;

    public PointerScanResultWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        ModuleFilters = _moduleFilters;
        DataContext = this;
        UpdatePageControls();
        UpdateActionState();
    }

    public ObservableCollection<PointerScanModuleFilterItem> ModuleFilters { get; }

    public void BeginNewScan()
    {
        OnNewScanClick(this, new RoutedEventArgs());
    }

    private async void OnNewScanClick(object sender, RoutedEventArgs eventArgs)
    {
        PointerScanOptions initialOptions = CreateInitialOptions();
        PointerScanSettingsDialog dialog = new(
            initialOptions,
            rescanMode: false)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.Options is null)
        {
            return;
        }

        _currentOptions = dialog.Options;
        await StartScanAsync(_currentOptions);
    }

    private async void OnRescanClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_result is null || _currentOptions is null || _isScanning)
        {
            return;
        }

        PointerScanSettingsDialog dialog = new(
            _currentOptions,
            rescanMode: true,
            _currentOptions.TargetAddress)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true ||
            dialog.Options is null ||
            dialog.RescanOptions is null)
        {
            return;
        }

        _currentOptions = dialog.Options;
        await RescanAsync(_result, dialog.RescanOptions);
    }

    private async Task StartScanAsync(PointerScanOptions options)
    {
        if (!EnsureTargetOpen())
        {
            return;
        }

        ReplaceCancellation();
        SetScanning(true, "正在扫描...");
        try
        {
            PointerScanResult result = await _viewModel.ScanPointersAsync(
                options,
                _scanCancellation!.Token);
            ApplyResult(result);
            StatusText.Text = result.Truncated
                ? $"扫描完成（结果已截断）：找到 {result.Chains.Count:N0} 条指针路径"
                : $"扫描完成：找到 {result.Chains.Count:N0} 条指针路径";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "扫描已取消";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"扫描失败：{exception.Message}";
        }
        finally
        {
            SetScanning(false, null);
        }
    }

    private async Task RescanAsync(
        PointerScanResult previous,
        PointerScanRescanOptions options)
    {
        if (!EnsureTargetOpen())
        {
            return;
        }

        ReplaceCancellation();
        SetScanning(true, "正在重扫描...");
        try
        {
            PointerScanResult result = await _viewModel.RescanPointersAsync(
                previous,
                options,
                _scanCancellation!.Token);
            ApplyResult(result);
            StatusText.Text = $"重扫描完成：剩余 {result.Chains.Count:N0} 条指针路径";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "重扫描已取消";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"重扫描失败：{exception.Message}";
        }
        finally
        {
            SetScanning(false, null);
        }
    }

    private void ApplyResult(PointerScanResult result)
    {
        _result = result;
        _filteredIndices.Clear();
        _usesModuleFilter = false;
        RebuildModuleFilters();
        _maxDepthDisplay = result.Chains.Count == 0
            ? 0
            : result.Chains.Max(chain => chain.Offsets.Count);
        _currentPage = 1;
        _totalPages = Math.Max(
            1,
            (int)Math.Ceiling(result.Chains.Count / (double)PageSize));
        RefreshPage();
        UpdateActionState();
    }

    private void RefreshPage()
    {
        if (_result is null)
        {
            ResultList.ItemsSource = Array.Empty<PointerScanPageRow>();
            UpdatePageControls();
            return;
        }

        IReadOnlyList<int> indices = _usesModuleFilter
            ? _filteredIndices
            : Enumerable.Range(0, _result.Chains.Count).ToArray();
        int start = (_currentPage - 1) * PageSize;
        int count = Math.Min(PageSize, Math.Max(0, indices.Count - start));
        List<PointerScanPageRow> rows = new(count);
        for (int index = 0; index < count; index++)
        {
            PointerChain chain = _result.Chains[indices[start + index]];
            rows.Add(CreatePageRow(chain, _maxDepthDisplay));
        }

        EnsureOffsetColumns(_maxDepthDisplay);
        ResultList.ItemsSource = rows;
        UpdatePageControls();
    }

    private PointerScanPageRow CreatePageRow(PointerChain chain, int depth)
    {
        string baseAddress = FormatBaseAddress(chain);
        string[] offsets = new string[depth];
        for (int index = 0; index < depth; index++)
        {
            int sourceIndex = chain.Offsets.Count - 1 - index;
            if (sourceIndex >= 0 && sourceIndex < chain.Offsets.Count)
            {
                int offset = chain.Offsets[sourceIndex];
                offsets[index] = offset >= 0
                    ? offset.ToString("X", CultureInfo.InvariantCulture)
                    : $"-{(-offset):X}";
            }
            else
            {
                offsets[index] = string.Empty;
            }
        }

        return new PointerScanPageRow
        {
            BaseAddress = baseAddress,
            PointsTo = ResolveAndFormat(chain),
            OffsetColumns = offsets,
            PointerExpression = BuildPointerExpression(chain)
        };
    }

    private string FormatBaseAddress(PointerChain chain)
    {
        if (string.IsNullOrWhiteSpace(chain.ModuleName))
        {
            return $"0x{chain.BaseAddress:X}";
        }

        ulong offset = chain.ModuleOffset;
        return $"\"{chain.ModuleName}\"+{offset:X}";
    }

    private string ResolveAndFormat(PointerChain chain)
    {
        if (!_viewModel.Target.IsOpen)
        {
            return "-";
        }

        ulong current;
        if (string.IsNullOrWhiteSpace(chain.ModuleName))
        {
            current = chain.BaseAddress;
        }
        else
        {
            ModuleDescriptor? module = _viewModel
                .EnumerateTargetModules()
                .FirstOrDefault(candidate =>
                    candidate.Name.Equals(
                        chain.ModuleName,
                        StringComparison.OrdinalIgnoreCase));
            if (module is null)
            {
                return "-";
            }

            current = module.BaseAddress + chain.ModuleOffset;
        }

        int pointerSize = _viewModel.Target.Is64Bit
            ? sizeof(ulong)
            : sizeof(uint);
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        for (int index = chain.Offsets.Count - 1; index >= 0; index--)
        {
            if (!_viewModel.Target.TryReadBytes(current, buffer[..pointerSize]))
            {
                return "-";
            }

            ulong pointer = _viewModel.Target.Is64Bit
                ? BitConverter.ToUInt64(buffer)
                : BitConverter.ToUInt32(buffer);
            if (pointer == 0)
            {
                return "-";
            }

            current = unchecked(
                pointer + (ulong)(long)chain.Offsets[index]);
        }

        return current.ToString("X8", CultureInfo.InvariantCulture);
    }

    private static string BuildPointerExpression(PointerChain chain)
    {
        string expression = string.IsNullOrWhiteSpace(chain.ModuleName)
            ? $"0x{chain.BaseAddress:X}"
            : $"\"{chain.ModuleName}\"+{chain.ModuleOffset:X}";
        for (int index = chain.Offsets.Count - 1; index >= 0; index--)
        {
            int offset = chain.Offsets[index];
            expression = offset >= 0
                ? $"[{expression}]+{offset:X}"
                : $"[{expression}]-{(-offset):X}";
        }

        return expression;
    }

    private void EnsureOffsetColumns(int depth)
    {
        while (ResultGridView.Columns.Count > 2)
        {
            ResultGridView.Columns.RemoveAt(2);
        }

        for (int index = 0; index < depth; index++)
        {
            ResultGridView.Columns.Add(new GridViewColumn
            {
                Header = $"偏移 {index}",
                Width = 80,
                DisplayMemberBinding = new Binding($"Offset{index}")
            });
        }
    }

    private void RebuildModuleFilters()
    {
        string[] selectedNames = _moduleFilters
            .Where(item => item.IsChecked)
            .Select(item => item.Name)
            .ToArray();
        _moduleFilters.Clear();
        if (_result is null)
        {
            return;
        }

        IEnumerable<string> names = _result.Chains
            .Select(chain => string.IsNullOrWhiteSpace(chain.ModuleName)
                ? "(无模块)"
                : chain.ModuleName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
        {
            _moduleFilters.Add(new PointerScanModuleFilterItem
            {
                Name = name,
                IsChecked = selectedNames.Length == 0 ||
                            selectedNames.Contains(
                                name,
                                StringComparer.OrdinalIgnoreCase)
            });
        }
    }

    private void ApplyModuleFilter()
    {
        _filteredIndices.Clear();
        if (_result is null)
        {
            return;
        }

        HashSet<string> selected = _moduleFilters
            .Where(item => item.IsChecked)
            .Select(item => item.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool selectAll = _moduleFilters.Count > 0 &&
                         selected.Count == _moduleFilters.Count;
        for (int index = 0; index < _result.Chains.Count; index++)
        {
            string name = string.IsNullOrWhiteSpace(
                _result.Chains[index].ModuleName)
                ? "(无模块)"
                : _result.Chains[index].ModuleName;
            if (selectAll || selected.Contains(name))
            {
                _filteredIndices.Add(index);
            }
        }

        _usesModuleFilter = true;
        _currentPage = 1;
        _totalPages = Math.Max(
            1,
            (int)Math.Ceiling(_filteredIndices.Count / (double)PageSize));
        RefreshPage();
        StatusText.Text = $"模块筛选后剩余 {_filteredIndices.Count:N0} 条结果";
    }

    private void OnSelectAllModulesClick(object sender, RoutedEventArgs eventArgs)
    {
        foreach (PointerScanModuleFilterItem item in _moduleFilters)
        {
            item.IsChecked = true;
        }
    }

    private void OnSelectNoModulesClick(object sender, RoutedEventArgs eventArgs)
    {
        foreach (PointerScanModuleFilterItem item in _moduleFilters)
        {
            item.IsChecked = false;
        }
    }

    private void OnApplyModuleFilterClick(object sender, RoutedEventArgs eventArgs)
    {
        ApplyModuleFilter();
        ModuleFilterToggle.IsChecked = false;
    }

    private void OnPreviousPageClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_currentPage <= 1)
        {
            return;
        }

        _currentPage--;
        RefreshPage();
    }

    private void OnNextPageClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_currentPage >= _totalPages)
        {
            return;
        }

        _currentPage++;
        RefreshPage();
    }

    private void OnCancelScanClick(object sender, RoutedEventArgs eventArgs)
    {
        _scanCancellation?.Cancel();
    }

    private void OnResultMouseDoubleClick(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton == MouseButton.Left &&
            ResultList.SelectedItem is PointerScanPageRow row)
        {
            AddToAddressList(row);
            eventArgs.Handled = true;
        }
    }

    private void OnResultPreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        DependencyObject? source = eventArgs.OriginalSource as DependencyObject;
        while (source is not null and not ListViewItem)
        {
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        if (source is ListViewItem item)
        {
            item.IsSelected = true;
        }
    }

    private void OnAddToAddressListClick(object sender, RoutedEventArgs eventArgs)
    {
        if (ResultList.SelectedItem is PointerScanPageRow row)
        {
            AddToAddressList(row);
        }
    }

    private void AddToAddressList(PointerScanPageRow row)
    {
        _viewModel.MemorySearchWorkspace.ActiveTab.SavedAddresses.Add(
            new PointerScanAddressEntry
            {
                Description = $"指针链 {row.BaseAddress}",
                Address = row.PointerExpression,
                Type = "8 Bytes",
                Value = row.PointsTo
            });
        StatusText.Text = "已添加到地址列表";
    }

    private void OnCopyPointerChainClick(object sender, RoutedEventArgs eventArgs)
    {
        if (ResultList.SelectedItem is PointerScanPageRow row)
        {
            Clipboard.SetText(row.PointerExpression);
            StatusText.Text = "已复制指针链";
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_result is null || _currentOptions is null)
        {
            return;
        }

        SaveFileDialog dialog = new()
        {
            Title = "保存指针扫描结果",
            Filter = "指针扫描结果 (*.dgps)|*.dgps",
            DefaultExt = ".dgps",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        PointerScanDocument document = new()
        {
            Options = _currentOptions,
            Result = _result
        };
        File.WriteAllText(
            dialog.FileName,
            JsonSerializer.Serialize(
                document,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }),
            new System.Text.UTF8Encoding(false));
        StatusText.Text = "指针扫描结果已保存";
    }

    private void OnLoadClick(object sender, RoutedEventArgs eventArgs)
    {
        OpenFileDialog dialog = new()
        {
            Title = "加载指针扫描结果",
            Filter = "指针扫描结果 (*.dgps)|*.dgps",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            PointerScanDocument? document = JsonSerializer.Deserialize<PointerScanDocument>(
                File.ReadAllText(dialog.FileName));
            if (document?.Options is null || document.Result is null)
            {
                throw new InvalidDataException("无效的指针扫描结果文件。");
            }

            _currentOptions = document.Options;
            ApplyResult(document.Result);
            StatusText.Text = $"已加载 {document.Result.Chains.Count:N0} 条结果";
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "加载失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private PointerScanOptions CreateInitialOptions()
    {
        ulong address = _currentOptions?.TargetAddress ??
                        _viewModel.SelectedInstruction?.Address ??
                        0;
        if (address == 0 &&
            TryParseHex(_viewModel.AddressInput, out ulong addressInput))
        {
            address = addressInput;
        }

        return _currentOptions ?? new PointerScanOptions
        {
            TargetAddress = address
        };
    }

    private bool EnsureTargetOpen()
    {
        if (_viewModel.Target.IsOpen)
        {
            return true;
        }

        StatusText.Text = "请先打开并附加目标进程。";
        return false;
    }

    private void ReplaceCancellation()
    {
        _scanCancellation?.Dispose();
        _scanCancellation = new CancellationTokenSource();
    }

    private void SetScanning(bool scanning, string? message)
    {
        _isScanning = scanning;
        CancelScanButton.Visibility = scanning
            ? Visibility.Visible
            : Visibility.Collapsed;
        ScanProgressBar.IsIndeterminate = scanning;
        ScanProgressBar.Value = 0;
        if (!string.IsNullOrWhiteSpace(message))
        {
            ProgressText.Text = message;
        }
        else if (!scanning)
        {
            ProgressText.Text = string.Empty;
        }

        UpdateActionState();
    }

    private void UpdateActionState()
    {
        NewScanButton.IsEnabled = !_isScanning;
        RescanButton.IsEnabled = !_isScanning && _result is not null;
        SaveButton.IsEnabled = !_isScanning;
        LoadButton.IsEnabled = !_isScanning;
        CancelScanButton.IsEnabled = _isScanning;
        PreviousPageButton.IsEnabled = !_isScanning && _currentPage > 1;
        NextPageButton.IsEnabled = !_isScanning && _currentPage < _totalPages;
    }

    private void UpdatePageControls()
    {
        int resultCount = _result?.Chains.Count ?? 0;
        if (_usesModuleFilter)
        {
            resultCount = _filteredIndices.Count;
        }

        CurrentPageRun.Text = _currentPage.ToString(CultureInfo.InvariantCulture);
        TotalPagesRun.Text = _totalPages.ToString(CultureInfo.InvariantCulture);
        ResultCountRun.Text = resultCount.ToString("N0", CultureInfo.InvariantCulture);
        UpdateActionState();
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
}

public sealed class PointerScanModuleFilterItem : INotifyPropertyChanged
{
    private bool _isChecked;

    public string Name { get; init; } = string.Empty;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
            {
                return;
            }

            _isChecked = value;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class PointerScanPageRow : ICustomTypeDescriptor
{
    public string BaseAddress { get; init; } = string.Empty;

    public string PointsTo { get; init; } = string.Empty;

    public string[] OffsetColumns { get; init; } = [];

    public string PointerExpression { get; init; } = string.Empty;

    public string this[int index] =>
        index >= 0 && index < OffsetColumns.Length
            ? OffsetColumns[index]
            : string.Empty;

    public AttributeCollection GetAttributes() => AttributeCollection.Empty;

    public string? GetClassName() => TypeDescriptor.GetClassName(this);

    public string? GetComponentName() => TypeDescriptor.GetComponentName(this);

    public TypeConverter? GetConverter() =>
        TypeDescriptor.GetConverter(this);

    public EventDescriptor? GetDefaultEvent() =>
        TypeDescriptor.GetDefaultEvent(this);

    public PropertyDescriptor? GetDefaultProperty() =>
        TypeDescriptor.GetDefaultProperty(this);

    public object? GetEditor(Type editorBaseType) =>
        TypeDescriptor.GetEditor(this, editorBaseType);

    public EventDescriptorCollection GetEvents() =>
        EventDescriptorCollection.Empty;

    public EventDescriptorCollection GetEvents(Attribute[]? attributes) =>
        EventDescriptorCollection.Empty;

    public PropertyDescriptorCollection GetProperties()
    {
        List<PropertyDescriptor> properties =
        [
            new PointerScanPagePropertyDescriptor(
                nameof(BaseAddress),
                static row => row.BaseAddress),
            new PointerScanPagePropertyDescriptor(
                nameof(PointsTo),
                static row => row.PointsTo),
            new PointerScanPagePropertyDescriptor(
                nameof(PointerExpression),
                static row => row.PointerExpression)
        ];
        for (int index = 0; index < OffsetColumns.Length; index++)
        {
            int capturedIndex = index;
            properties.Add(new PointerScanPagePropertyDescriptor(
                $"Offset{index}",
                row => row[capturedIndex]));
        }

        return new PropertyDescriptorCollection(properties.ToArray());
    }

    public PropertyDescriptorCollection GetProperties(Attribute[]? attributes) =>
        GetProperties();

    public object GetPropertyOwner(PropertyDescriptor? propertyDescriptor) => this;

    private sealed class PointerScanPagePropertyDescriptor : PropertyDescriptor
    {
        private readonly Func<PointerScanPageRow, object?> _getter;

        public PointerScanPagePropertyDescriptor(
            string name,
            Func<PointerScanPageRow, object?> getter)
            : base(name, null)
        {
            _getter = getter;
        }

        public override Type ComponentType => typeof(PointerScanPageRow);

        public override bool IsReadOnly => true;

        public override Type PropertyType => typeof(string);

        public override bool CanResetValue(object component) => false;

        public override object? GetValue(object? component) =>
            component is PointerScanPageRow row
                ? _getter(row)
                : null;

        public override void ResetValue(object component)
        {
        }

        public override void SetValue(object? component, object? value)
        {
        }

        public override bool ShouldSerializeValue(object component) => false;
    }
}

public sealed class PointerScanAddressEntry
{
    public string Description { get; init; } = string.Empty;

    public string Address { get; init; } = string.Empty;

    public string Type { get; init; } = "8 Bytes";

    public string Value { get; init; } = string.Empty;
}

internal sealed class PointerScanDocument
{
    public PointerScanOptions? Options { get; init; }

    public PointerScanResult? Result { get; init; }
}
