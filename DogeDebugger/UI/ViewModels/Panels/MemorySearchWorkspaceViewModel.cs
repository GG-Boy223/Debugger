using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DogeDebugger.Debugger.Session;

namespace DogeDebugger.UI.ViewModels.Panels;

public partial class MemorySearchWorkspaceViewModel : ObservableObject
{
    private readonly DebuggerSession _session;
    private readonly Action<ulong> _navigateToAddress;
    private int _nextTabNumber = 2;

    [ObservableProperty]
    private MemorySearchViewModel _activeTab = null!;

    public MemorySearchWorkspaceViewModel(
        DebuggerSession session,
        Action<ulong> navigateToAddress)
    {
        _session = session;
        _navigateToAddress = navigateToAddress;

        MemorySearchViewModel firstTab = CreateTab(1);
        Tabs.Add(firstTab);
        ActiveTab = firstTab;
    }

    public ObservableCollection<MemorySearchViewModel> Tabs { get; } = [];

    public bool IsTabStripVisible => Tabs.Count > 1;

    [RelayCommand]
    private void NewTab()
    {
        MemorySearchViewModel tab = CreateTab(_nextTabNumber++);
        tab.CopyOptionsFrom(ActiveTab);
        Tabs.Add(tab);
        ActiveTab = tab;
        OnPropertyChanged(nameof(IsTabStripVisible));
    }

    [RelayCommand]
    private void CloseTab(MemorySearchViewModel? tab)
    {
        if (tab is null || Tabs.Count <= 1)
        {
            return;
        }

        int index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        Tabs.RemoveAt(index);
        if (ReferenceEquals(ActiveTab, tab))
        {
            ActiveTab = Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
        }

        OnPropertyChanged(nameof(IsTabStripVisible));
    }

    public void ResetAll()
    {
        foreach (MemorySearchViewModel tab in Tabs)
        {
            tab.ResetCommand.Execute(null);
        }
    }

    public void RefreshTargetState()
    {
        foreach (MemorySearchViewModel tab in Tabs)
        {
            tab.RefreshTargetState();
        }
    }

    private MemorySearchViewModel CreateTab(int number)
    {
        return new MemorySearchViewModel(_session, _navigateToAddress)
        {
            Title = $"搜索 {number}"
        };
    }
}
