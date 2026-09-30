using System.Windows.Controls;

namespace DogeDebugger.UI.ViewModels.Dialogs.CacheManager;

/// <summary>
/// One page hosted by the cache manager dialog. Mirrors the original
/// <c>JCA12DBF</c> tab contract: header, item count and view instance.
/// </summary>
public interface ICacheManagerTabViewModel
{
    string Header { get; }

    int Count { get; }

    UserControl View { get; }
}
