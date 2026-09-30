using System.Windows;

namespace DogeDebugger.PluginSdk;

public interface IPlugin
{
    bool DeclaresMcpTool { get; }

    ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default);
}

public interface IPluginContext
{
    PluginManifest Manifest { get; }

    IPluginLogger Logger { get; }

    IPluginCommandBus Commands { get; }

    IPluginEventBus Events { get; }

    IPluginMemory Memory { get; }

    IPluginDebug Debugger { get; }

    IPluginDisassembly Disassembly { get; }

    IPluginSearch Search { get; }

    IPluginUserInterface UserInterface { get; }

    IPluginStorage Storage { get; }
}

public interface IPluginLogger
{
    void Info(string message);

    void Warning(string message);

    void Error(string message);
}

public interface IPluginCommandBus
{
    ValueTask<string> CallAsync(
        string name,
        string argumentsJson = "{}",
        CancellationToken cancellationToken = default);

    IReadOnlyList<PluginCommandDescriptor> ListTools();
}

public interface IPluginEventBus
{
    IDisposable Subscribe(
        string eventName,
        Func<PluginEvent, CancellationToken, ValueTask> handler);

    IDisposable SubscribeBreakpointFilter(
        Func<PluginBreakpointHitEvent, CancellationToken, ValueTask<PluginBreakpointDecision>> handler);
}

public interface IPluginMemory
{
    ValueTask<byte[]> ReadBytesAsync(
        ulong address,
        int size,
        CancellationToken cancellationToken = default);

    ValueTask<bool> WriteBytesAsync(
        ulong address,
        byte[] bytes,
        CancellationToken cancellationToken = default);

    ValueTask<int?> ReadInt32Async(
        ulong address,
        CancellationToken cancellationToken = default);

    ValueTask<ulong?> ReadUInt64Async(
        ulong address,
        CancellationToken cancellationToken = default);
}

public interface IPluginDebug
{
    bool IsPaused { get; }

    bool IsDebugging { get; }

    ValueTask ContinueAsync(CancellationToken cancellationToken = default);

    ValueTask BreakAsync(CancellationToken cancellationToken = default);

    ValueTask StepIntoAsync(CancellationToken cancellationToken = default);

    ValueTask StepOverAsync(CancellationToken cancellationToken = default);

    ValueTask StepOutAsync(CancellationToken cancellationToken = default);
}

public interface IPluginDisassembly
{
    ValueTask<PluginDisassemblySelection> GetSelectionAsync(
        CancellationToken cancellationToken = default);
}

public interface IPluginSearch
{
    ValueTask<IReadOnlyList<PluginModuleInfo>> GetModulesAsync(
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<PluginMemoryRegionInfo>> GetMemoryRegionsAsync(
        CancellationToken cancellationToken = default);

    ValueTask<string> GenerateSignatureAsync(
        PluginSignatureOptions options,
        CancellationToken cancellationToken = default);

    ValueTask<PluginSearchResult> RunAsync(
        PluginSearchRequest request,
        CancellationToken cancellationToken = default);
}

public interface IPluginUserInterface
{
    void RegisterPanel(string id, string title, Func<FrameworkElement> panelFactory);

    void RegisterMenu(string header, Action<IPluginMenuBuilder> configure);

    void RegisterContextMenu(
        PluginContextMenuTarget target,
        string header,
        Action<IPluginMenuBuilder> configure);

    bool RegisterHotkey(
        string id,
        string displayName,
        PluginKeyGesture gesture,
        Func<CancellationToken, ValueTask> handler);

    void UnregisterHotkey(string id);

    void OpenPanel(string id);

    void NavigateToDisassembly(ulong address);

    void NavigateToHex(ulong address);
}

public interface IPluginStorage
{
    string Directory { get; }

    ValueTask<string?> GetJsonAsync(
        string key,
        CancellationToken cancellationToken = default);

    ValueTask SetJsonAsync(
        string key,
        string json,
        CancellationToken cancellationToken = default);

    ValueTask RemoveAsync(
        string key,
        CancellationToken cancellationToken = default);
}

public interface IPluginMenuBuilder
{
    void AddItem(string header, Func<CancellationToken, ValueTask> handler);

    void AddItem(
        string header,
        string? hotkeyId,
        Func<CancellationToken, ValueTask> handler);

    void AddSubMenu(string header, Action<IPluginMenuBuilder> configure);

    void AddSeparator();
}

public interface IPluginMcpToolRegistrar
{
    void RegisterTool(PluginMcpToolDescriptor descriptor);
}
