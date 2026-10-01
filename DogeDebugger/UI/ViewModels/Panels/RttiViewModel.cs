using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DogeDebugger.Core.Rtti;
using DogeDebugger.Debugger.Session;
using Microsoft.Win32;

namespace DogeDebugger.UI.ViewModels.Panels;

public partial class RttiViewModel : ObservableObject, IDisposable
{
    private readonly DebuggerSession _session;
    private readonly DispatcherTimer _refreshTimer;
    private bool _suppressSelectionRefresh;
    private bool _disposed;

    [ObservableProperty]
    private RttiTypeInfo? _selectedType;

    [ObservableProperty]
    private RttiClassDefinition? _selectedClass;

    [ObservableProperty]
    private RttiFieldValue? _selectedField;

    [ObservableProperty]
    private string _addressText = "0x140000000";

    [ObservableProperty]
    private string _statusText = "尚未扫描 RTTI 类型。";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _autoRefresh = true;

    [ObservableProperty]
    private bool _allowEditValues;

    [ObservableProperty]
    private int _refreshIntervalMs = 250;

    public RttiViewModel(DebuggerSession session)
    {
        _session = session;
        _refreshTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(RefreshIntervalMs),
            DispatcherPriority.Background,
            HandleRefreshTimer,
            Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher);
        _refreshTimer.Start();
        _session.RttiWorkspace.Changed += HandleWorkspaceChanged;
        RefreshClasses();
    }

    public ObservableCollection<RttiTypeInfo> Types { get; } = [];

    public ObservableCollection<RttiClassDefinition> Classes { get; } = [];

    public ObservableCollection<RttiFieldValue> Fields { get; } = [];

    public bool HasSelectedClass => SelectedClass is not null;

    public bool HasFields => Fields.Count > 0;

    public void OnTargetChanged()
    {
        Fields.Clear();
        SelectedField = null;
        StatusText = _session.Target.IsOpen
            ? "目标进程已切换，请重新读取 RTTI 结构。"
            : "尚未打开目标进程。";
    }

    public void NavigateToAddress(ulong address)
    {
        AddressText = $"0x{address:X}";
        StatusText = $"已定位到指令访问地址 0x{address:X}。";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshTimer.Stop();
        _refreshTimer.Tick -= HandleRefreshTimer;
        _session.RttiWorkspace.Changed -= HandleWorkspaceChanged;
        GC.SuppressFinalize(this);
    }

    [RelayCommand]
    private async Task ScanTypesAsync()
    {
        if (!_session.Target.IsOpen)
        {
            StatusText = "请先打开目标进程。";
            return;
        }

        IsBusy = true;
        try
        {
            RttiScanResult result = await Task.Run(
                    () => _session.ScanRtti(),
                    CancellationToken.None)
                .ConfigureAwait(true);
            Types.Clear();
            foreach (RttiTypeInfo type in result.Types)
            {
                Types.Add(type);
            }

            StatusText = result.Truncated
                ? $"已扫描 {result.ScannedModuleCount} 个模块，发现 {Types.Count} 个类型（候选 {result.CandidateTypeCount}，结果已截断）。"
                : $"已扫描 {result.ScannedModuleCount} 个模块，发现 {Types.Count} 个类型（候选 {result.CandidateTypeCount}）。";
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CreateClass()
    {
        RttiClassDefinition definition = new()
        {
            Name = CreateUniqueClassName("Struct"),
            Comment = "手动创建",
            BaseAddress = TryParseAddress(AddressText, out ulong address) ? address : 0,
            IsExpanded = true
        };
        for (int offset = 0; offset < 64; offset++)
        {
            definition.Fields.Add(new RttiFieldDefinition
            {
                Name = $"field_{offset:X}",
                Offset = offset,
                Type = RttiNodeType.Hex8,
                Comment = "手动字段"
            });
        }

        definition.RecalculateOffsets(_session.Target.Is64Bit);
        _session.RttiWorkspace.CreateStruct(
            definition.Name,
            definition.BaseAddress,
            definition.Comment,
            definition.Fields,
            _session.Target.Is64Bit);
        RefreshClasses();
        SelectedClass = Classes.FirstOrDefault(item => item.Id == definition.Id);
        StatusText = $"已创建结构 {definition.Name}。";
    }

    [RelayCommand]
    private async Task AutoGuessAsync()
    {
        if (!_session.Target.IsOpen)
        {
            StatusText = "请先打开目标进程。";
            return;
        }

        if (!TryParseAddress(AddressText, out ulong address) || address == 0)
        {
            StatusText = "请输入有效的非零十六进制地址。";
            return;
        }

        RttiAutoGuessLengthDialog dialog = new(256)
        {
            Owner = Application.Current?.MainWindow
        };
        if (dialog.ShowDialog() != true)
        {
            StatusText = "已取消智能解析。";
            return;
        }

        IsBusy = true;
        try
        {
            RttiClassDefinition definition = await Task.Run(
                    () => _session.RttiWorkspace.AutoGuess(
                        _session.Target,
                        CreateUniqueClassName($"Auto_{address:X}"),
                        address,
                        dialog.ReadLength),
                    CancellationToken.None)
                .ConfigureAwait(true);
            RefreshClasses();
            SelectedClass = Classes.FirstOrDefault(item => item.Id == definition.Id);
            StatusText = $"智能解析完成，已生成 {definition.Fields.Count} 个字段。";
            await RefreshValuesAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            StatusText = $"RTTI 智能解析失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void RemoveSelectedClass()
    {
        RttiClassDefinition? definition = SelectedClass;
        if (definition is null)
        {
            return;
        }

        _session.RttiWorkspace.RemoveStruct(definition.Name);
        RefreshClasses();
        SelectedClass = Classes.FirstOrDefault();
        StatusText = $"已删除结构 {definition.Name}。";
    }

    [RelayCommand]
    private async Task ApplyAddressAsync()
    {
        if (SelectedClass is null)
        {
            StatusText = "请先选择一个 RTTI 结构。";
            return;
        }

        if (!TryParseAddress(AddressText, out ulong address) || address == 0)
        {
            StatusText = "请输入有效的非零十六进制地址。";
            return;
        }

        SelectedClass.BaseAddress = address;
        await RefreshValuesAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RefreshValuesAsync()
    {
        if (!_session.Target.IsOpen)
        {
            StatusText = "请先打开目标进程。";
            return;
        }

        RttiClassDefinition? definition = SelectedClass;
        if (definition is null)
        {
            StatusText = "请先选择一个 RTTI 结构。";
            return;
        }

        if (!TryParseAddress(AddressText, out ulong address) || address == 0)
        {
            address = definition.BaseAddress;
        }

        if (address == 0)
        {
            StatusText = "结构基地址为 0，无法读取。";
            return;
        }

        IsBusy = true;
        try
        {
            RttiInstance instance = await Task.Run(
                    () => _session.ReadRttiInstance(definition, address),
                    CancellationToken.None)
                .ConfigureAwait(true);
            IReadOnlyList<RttiFieldValue> values = instance.HasMemory
                ? await Task.Run(
                        () => RttiMemoryReader.ReadValues(_session.Target, instance),
                        CancellationToken.None)
                    .ConfigureAwait(true)
                : [];
            Fields.Clear();
            foreach (RttiFieldValue value in values)
            {
                Fields.Add(value);
            }

            SelectedField = Fields.FirstOrDefault();
            StatusText = instance.HasMemory
                ? $"已读取 {Fields.Count} 个字段，基址 0x{address:X}。"
                : instance.StatusText;
        }
        catch (Exception exception)
        {
            Fields.Clear();
            StatusText = exception.Message;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasFields));
        }
    }

    [RelayCommand]
    private void AddSelectedField()
    {
        RttiClassDefinition? definition = SelectedClass;
        if (definition is null)
        {
            return;
        }

        RttiFieldDefinition field = new()
        {
            Name = $"field_{definition.Fields.Count:X}",
            Type = RttiNodeType.Hex64,
            Comment = "手动添加字段"
        };
        _session.RttiWorkspace.AddField(
            definition.Name,
            field,
            _session.Target.Is64Bit);
        StatusText = $"已向 {definition.Name} 添加字段。";
    }

    [RelayCommand]
    private void SaveProject()
    {
        SaveFileDialog dialog = new()
        {
            Filter = "RTTI Project (*.rtti)|*.rtti|All Files (*.*)|*.*",
            DefaultExt = ".rtti",
            AddExtension = true
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _session.RttiWorkspace.SaveProject(dialog.FileName);
            StatusText = $"RTTI project saved to: {dialog.FileName}";
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }
    }

    [RelayCommand]
    private void LoadProject()
    {
        OpenFileDialog dialog = new()
        {
            Filter = "RTTI Project (*.rtti)|*.rtti|All Files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _session.RttiWorkspace.LoadProject(dialog.FileName, _session.Target.Is64Bit);
            RefreshClasses();
            SelectedClass = Classes.FirstOrDefault();
            StatusText = $"已加载 RTTI 工程文件：{dialog.FileName}";
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }
    }

    partial void OnSelectedClassChanged(RttiClassDefinition? value)
    {
        OnPropertyChanged(nameof(HasSelectedClass));
        if (value is null)
        {
            Fields.Clear();
            SelectedField = null;
            return;
        }

        if (_suppressSelectionRefresh)
        {
            return;
        }

        if (value.BaseAddress != 0)
        {
            AddressText = $"0x{value.BaseAddress:X}";
        }

        _ = RefreshValuesAsync();
    }

    partial void OnSelectedTypeChanged(RttiTypeInfo? value)
    {
        if (value is null || value.VftableAddress == 0)
        {
            return;
        }

        AddressText = $"0x{value.VftableAddress:X}";
    }

    partial void OnAutoRefreshChanged(bool value)
    {
        if (value)
        {
            _refreshTimer.Start();
        }
        else
        {
            _refreshTimer.Stop();
        }
    }

    partial void OnRefreshIntervalMsChanged(int value)
    {
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(value, 50, 5000));
    }

    private void HandleWorkspaceChanged()
    {
        if (Application.Current?.Dispatcher is { } dispatcher &&
            !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(RefreshClasses);
            return;
        }

        RefreshClasses();
    }

    private void RefreshClasses()
    {
        Guid? selectedId = SelectedClass?.Id;
        _suppressSelectionRefresh = true;
        try
        {
            Classes.Clear();
            foreach (RttiClassDefinition definition in _session.RttiWorkspace.GetClasses())
            {
                Classes.Add(definition);
            }

            SelectedClass = selectedId.HasValue
                ? Classes.FirstOrDefault(item => item.Id == selectedId.Value) ??
                  Classes.FirstOrDefault()
                : Classes.FirstOrDefault();
        }
        finally
        {
            _suppressSelectionRefresh = false;
        }
    }

    private void HandleRefreshTimer(object? sender, EventArgs eventArgs)
    {
        if (AutoRefresh && SelectedClass is not null && _session.Target.IsOpen)
        {
            _ = RefreshValuesAsync();
        }
    }

    private string CreateUniqueClassName(string prefix)
    {
        HashSet<string> names = Classes
            .Select(item => item.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(prefix))
        {
            return prefix;
        }

        int suffix = 2;
        while (names.Contains($"{prefix}_{suffix}"))
        {
            suffix++;
        }

        return $"{prefix}_{suffix}";
    }

    private static bool TryParseAddress(string text, out ulong address)
    {
        address = 0;
        text = text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(
                text[2..],
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out address)
            : ulong.TryParse(
                  text,
                  NumberStyles.HexNumber,
                  CultureInfo.InvariantCulture,
                  out address);
    }
}

public sealed class RttiAutoGuessLengthDialog : Window
{
    private readonly System.Windows.Controls.TextBox _lengthBox;

    public RttiAutoGuessLengthDialog(int initialLength)
    {
        Title = "自动猜测 RTTI";
        Width = 360;
        Height = 150;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        Grid root = new()
        {
            Margin = new Thickness(14)
        };
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
        {
            Height = GridLength.Auto
        });
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
        {
            Height = GridLength.Auto
        });
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
        {
            Height = GridLength.Auto
        });

        System.Windows.Controls.TextBlock label = new()
        {
            Text = "读取长度（1 ~ 65536 字节）"
        };
        root.Children.Add(label);

        _lengthBox = new System.Windows.Controls.TextBox
        {
            Text = Math.Clamp(initialLength, 1, 65_536).ToString(
                CultureInfo.InvariantCulture),
            Margin = new Thickness(0, 8, 0, 12)
        };
        Grid.SetRow(_lengthBox, 1);
        root.Children.Add(_lengthBox);

        StackPanel buttons = new()
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        System.Windows.Controls.Button cancel = new()
        {
            Content = "取消",
            Width = 80,
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancel.Click += (_, _) =>
        {
            DialogResult = false;
            Close();
        };
        buttons.Children.Add(cancel);

        System.Windows.Controls.Button accept = new()
        {
            Content = "确定",
            Width = 80,
            IsDefault = true
        };
        accept.Click += (_, _) => Confirm();
        buttons.Children.Add(accept);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        Content = root;
    }

    public int ReadLength { get; private set; } = 256;

    private void Confirm()
    {
        if (!int.TryParse(
                _lengthBox.Text.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int value) ||
            value is < 1 or > 65_536)
        {
            MessageBox.Show(
                this,
                "请输入 1 ~ 65536 之间的读取长度。",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        ReadLength = value;
        DialogResult = true;
        Close();
    }
}
