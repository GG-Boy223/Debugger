using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DogeDebugger.UI.ViewModels.Dialogs.CacheManager;

namespace DogeDebugger.UI.ViewModels.Dialogs;

/// <summary>
/// Hosts the cache manager tabs. The original dialog exposes a single
/// "静态缓存" tab, so the model keeps the same open-ended tab contract.
/// </summary>
public partial class CacheManagerViewModel : ObservableObject
{
    public CacheManagerViewModel()
    {
        Tabs.Add(new StaticAnalysisCacheTabViewModel());
        SelectedTab = Tabs.FirstOrDefault();
    }

    public ObservableCollection<ICacheManagerTabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    private ICacheManagerTabViewModel? _selectedTab;
}
