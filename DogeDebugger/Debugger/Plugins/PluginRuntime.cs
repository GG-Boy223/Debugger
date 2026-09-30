using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Windows;
using DogeDebugger.Core.AI;
using DogeDebugger.Debugger.Session;
using DogeDebugger.Debugger.UserMode;
using DogeDebugger.PluginSdk;

namespace DogeDebugger.Debugger.Plugins;

public sealed class PluginRuntime : IAsyncDisposable
{
    private const uint BreakpointExceptionCode = 0x80000003;

    private readonly DebuggerSession _session;
    private readonly PluginManifestReader _manifestReader = new();
    private readonly McpToolRegistry? _mcpTools;
    private readonly List<LoadedPlugin> _plugins = [];
    private readonly object _subscriptionGate = new();
    private readonly Dictionary<string, List<PluginEventSubscription>> _eventSubscriptions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PluginBreakpointFilterSubscription> _breakpointFilters = [];
    private readonly Dictionary<string, PluginHotkeyRegistration> _hotkeys =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public PluginRuntime(DebuggerSession session)
        : this(session, mcpTools: null)
    {
    }

    public PluginRuntime(DebuggerSession session, McpToolRegistry? mcpTools)
    {
        _session = session;
        _mcpTools = mcpTools;
        _session.TargetChanged += HandleTargetChanged;
        _session.Paused += HandleDebuggerPaused;
        _session.DebugEventReceived += HandleDebugEventReceived;
        _session.StateChanged += HandleDebuggerStateChanged;
        _session.Debugger.BreakpointFilterRequested += HandleBreakpointFilterRequested;
    }

    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;

    public bool IsLoaded(string pluginId) =>
        _plugins.Any(plugin =>
            string.Equals(
                plugin.Descriptor.Manifest.Id,
                pluginId,
                StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<PluginDescriptor> DiscoverPlugins(string pluginDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);
        if (!Directory.Exists(pluginDirectory))
        {
            return [];
        }

        List<PluginDescriptor> descriptors = [];
        foreach (string manifestPath in Directory.GetFiles(
                     pluginDirectory,
                     "plugin.json",
                     SearchOption.AllDirectories))
        {
            try
            {
                descriptors.Add(_manifestReader.Read(manifestPath));
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Plugin manifest failed: {manifestPath}: {exception}");
            }
        }

        return descriptors
            .OrderBy(descriptor => descriptor.Manifest.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<LoadedPlugin> LoadPluginAsync(
        PluginDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (IsLoaded(descriptor.Manifest.Id))
        {
            return _plugins.First(plugin =>
                string.Equals(
                    plugin.Descriptor.Manifest.Id,
                    descriptor.Manifest.Id,
                    StringComparison.OrdinalIgnoreCase));
        }

        LoadedPlugin plugin = await LoadAsync(descriptor, cancellationToken)
            .ConfigureAwait(false);
        _plugins.Add(plugin);
        return plugin;
    }

    public async Task<bool> UnloadPluginAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LoadedPlugin? plugin = _plugins.FirstOrDefault(candidate =>
            string.Equals(
                candidate.Descriptor.Manifest.Id,
                pluginId,
                StringComparison.OrdinalIgnoreCase));
        if (plugin is null)
        {
            return false;
        }

        _plugins.Remove(plugin);
        await plugin.DisposeAsync().ConfigureAwait(false);
        RemovePluginRegistrations(pluginId);
        PluginUnloaded?.Invoke(pluginId);
        return true;
    }

    public event Action<PluginPanelRegistration>? PanelRegistered;

    public event Action<string>? PanelUnregistered;

    public event Action<PluginMenuRegistration>? MenuRegistered;

    public event Action<string>? MenuUnregistered;

    public event Action<PluginHotkeyRegistration>? HotkeyRegistered;

    public event Action<string, string>? HotkeyUnregistered;

    public event Action<string, string>? PanelOpenRequested;

    public event Action<string>? PluginUnloaded;

    public event Action<string, ulong>? DisassemblyNavigationRequested;

    public event Action<string, ulong>? HexNavigationRequested;

    public async Task LoadDirectoryAsync(
        string pluginDirectory,
        IReadOnlyDictionary<string, bool> enabledStates,
        CancellationToken cancellationToken = default)
    {
        foreach (PluginDescriptor descriptor in DiscoverPlugins(pluginDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!enabledStates.TryGetValue(descriptor.Manifest.Id, out bool enabled) || !enabled)
            {
                continue;
            }

            try
            {
                await LoadPluginAsync(descriptor, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Plugin load failed: {descriptor.Manifest.Id}: {exception}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.TargetChanged -= HandleTargetChanged;
        _session.Paused -= HandleDebuggerPaused;
        _session.DebugEventReceived -= HandleDebugEventReceived;
        _session.StateChanged -= HandleDebuggerStateChanged;
        _session.Debugger.BreakpointFilterRequested -= HandleBreakpointFilterRequested;

        foreach (LoadedPlugin plugin in _plugins)
        {
            await plugin.DisposeAsync().ConfigureAwait(false);
        }

        _plugins.Clear();
        lock (_subscriptionGate)
        {
            _eventSubscriptions.Clear();
            _breakpointFilters.Clear();
            _hotkeys.Clear();
        }
    }

    private void RemovePluginRegistrations(string pluginId)
    {
        List<string> hotkeyIds = [];
        lock (_subscriptionGate)
        {
            foreach ((string eventName, List<PluginEventSubscription> handlers) in
                     _eventSubscriptions.ToArray())
            {
                handlers.RemoveAll(subscription =>
                    string.Equals(
                        subscription.PluginId,
                        pluginId,
                        StringComparison.OrdinalIgnoreCase));
                if (handlers.Count == 0)
                {
                    _eventSubscriptions.Remove(eventName);
                }
            }

            _breakpointFilters.RemoveAll(subscription =>
                string.Equals(
                    subscription.PluginId,
                    pluginId,
                    StringComparison.OrdinalIgnoreCase));

            foreach ((string key, PluginHotkeyRegistration registration) in _hotkeys.ToArray())
            {
                if (string.Equals(
                        registration.PluginId,
                        pluginId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    _hotkeys.Remove(key);
                    hotkeyIds.Add(registration.Id);
                }
            }
        }

        foreach (string hotkeyId in hotkeyIds)
        {
            OnHotkeyUnregistered(pluginId, hotkeyId);
        }

        PanelUnregistered?.Invoke(pluginId);
        MenuUnregistered?.Invoke(pluginId);
    }

    private async Task<LoadedPlugin> LoadAsync(
        PluginDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        if (descriptor.Kind != PluginPackageKind.Managed)
        {
            throw new NotSupportedException(
                "Web plugins require the desktop WebView host and are loaded by the UI layer.");
        }

        PluginLoadContext loadContext = new(descriptor.EntryPath);
        Assembly assembly = loadContext.LoadFromAssemblyPath(descriptor.EntryPath);
        Type pluginType = ResolvePluginType(assembly, descriptor.Manifest.PluginClass);
        IPlugin plugin = Activator.CreateInstance(pluginType) as IPlugin
            ?? throw new InvalidOperationException(
                $"Could not create plugin type '{pluginType.FullName}'.");

        PluginContext context = new(this, descriptor.Manifest, descriptor.Directory);
        await plugin.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
        return new LoadedPlugin(descriptor, loadContext, plugin);
    }

    private static Type ResolvePluginType(Assembly assembly, string? configuredTypeName)
    {
        if (!string.IsNullOrWhiteSpace(configuredTypeName))
        {
            Type? configured = assembly.GetType(configuredTypeName, throwOnError: false);
            if (configured is not null &&
                typeof(IPlugin).IsAssignableFrom(configured) &&
                !configured.IsAbstract)
            {
                return configured;
            }

            throw new InvalidOperationException(
                $"Plugin class '{configuredTypeName}' was not found or does not implement IPlugin.");
        }

        Type[] implementations = assembly
            .GetExportedTypes()
            .Where(type =>
                typeof(IPlugin).IsAssignableFrom(type) &&
                !type.IsAbstract &&
                type.GetConstructor(Type.EmptyTypes) is not null)
            .ToArray();
        return implementations.Length switch
        {
            0 => throw new InvalidOperationException("No public IPlugin implementation was found."),
            1 => implementations[0],
            _ => throw new InvalidOperationException(
                "Multiple IPlugin implementations found. Set pluginClass in plugin.json.")
        };
    }

    private void OnPanelRegistered(PluginPanelRegistration registration) =>
        PanelRegistered?.Invoke(registration);

    private void OnMenuRegistered(PluginMenuRegistration registration) =>
        MenuRegistered?.Invoke(registration);

    private void OnHotkeyRegistered(PluginHotkeyRegistration registration) =>
        HotkeyRegistered?.Invoke(registration);

    private void OnHotkeyUnregistered(string pluginId, string hotkeyId) =>
        HotkeyUnregistered?.Invoke(pluginId, hotkeyId);

    private void OnPanelOpenRequested(string pluginId, string panelId) =>
        PanelOpenRequested?.Invoke(pluginId, panelId);

    private void OnDisassemblyNavigationRequested(string pluginId, ulong address) =>
        DisassemblyNavigationRequested?.Invoke(pluginId, address);

    private void OnHexNavigationRequested(string pluginId, ulong address) =>
        HexNavigationRequested?.Invoke(pluginId, address);

    private IDisposable Subscribe(
        string pluginId,
        string eventName,
        Func<PluginEvent, CancellationToken, ValueTask> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(handler);

        PluginEventSubscription subscription = new(pluginId, eventName, handler);
        lock (_subscriptionGate)
        {
            if (!_eventSubscriptions.TryGetValue(eventName, out List<PluginEventSubscription>? handlers))
            {
                handlers = [];
                _eventSubscriptions[eventName] = handlers;
            }

            handlers.Add(subscription);
        }

        return new PluginSubscription(() => RemoveEventSubscription(eventName, subscription));
    }

    private IDisposable SubscribeBreakpointFilter(
        string pluginId,
        Func<PluginBreakpointHitEvent, CancellationToken, ValueTask<PluginBreakpointDecision>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        PluginBreakpointFilterSubscription subscription = new(pluginId, handler);
        lock (_subscriptionGate)
        {
            _breakpointFilters.Add(subscription);
        }

        return new PluginSubscription(() => RemoveBreakpointFilter(subscription));
    }

    private void RemoveEventSubscription(string eventName, PluginEventSubscription subscription)
    {
        lock (_subscriptionGate)
        {
            if (_eventSubscriptions.TryGetValue(
                    eventName,
                    out List<PluginEventSubscription>? handlers))
            {
                handlers.Remove(subscription);
                if (handlers.Count == 0)
                {
                    _eventSubscriptions.Remove(eventName);
                }
            }
        }
    }

    private void RemoveBreakpointFilter(PluginBreakpointFilterSubscription subscription)
    {
        lock (_subscriptionGate)
        {
            _breakpointFilters.Remove(subscription);
        }
    }

    private void Publish(string eventName, object payload)
    {
        PluginEventSubscription[] subscriptions;
        lock (_subscriptionGate)
        {
            if (!_eventSubscriptions.TryGetValue(
                    eventName,
                    out List<PluginEventSubscription>? handlers) ||
                handlers.Count == 0)
            {
                return;
            }

            subscriptions = handlers.ToArray();
        }

        PluginEvent pluginEvent = new()
        {
            Name = eventName,
            Data = JsonSerializer.SerializeToElement(payload)
        };

        foreach (PluginEventSubscription subscription in subscriptions)
        {
            _ = InvokePluginEventHandlerAsync(subscription, pluginEvent);
        }
    }

    private static async Task InvokePluginEventHandlerAsync(
        PluginEventSubscription subscription,
        PluginEvent pluginEvent)
    {
        try
        {
            await subscription.Handler(pluginEvent, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                $"Plugin event handler failed: {subscription.PluginId}/{pluginEvent.Name}: {exception}");
        }
    }

    private void HandleTargetChanged(object? sender, EventArgs args)
    {
        Publish(
            "debugger.attached",
            new
            {
                processId = _session.Target.ProcessId,
                processName = _session.Target.ProcessName,
                path = _session.Target.FilePath,
                is64Bit = _session.Target.Is64Bit
            });
    }

    private void HandleDebuggerPaused(object? sender, DebuggerPausedEventArgs args)
    {
        Publish(
            "debugger.paused",
            new
            {
                reason = args.Reason.ToString(),
                processId = args.Event.ProcessId,
                threadId = args.Event.ThreadId,
                address = FormatAddress(args.Event.Address),
                instructionPointer = FormatAddress(args.Registers.InstructionPointer),
                exceptionCode = args.Event.ExceptionCode
            });
    }

    private void HandleDebuggerStateChanged(
        object? sender,
        DebuggerStateChangedEventArgs args)
    {
        if (args.IsDebugging && !args.IsPaused)
        {
            Publish("debugger.resumed", new { processId = _session.Target.ProcessId });
        }
        else if (!args.IsDebugging)
        {
            Publish("debugger.detached", new { processId = _session.Target.ProcessId });
        }
    }

    private void HandleDebugEventReceived(object? sender, DebuggerEventEventArgs args)
    {
        DebuggerEventRecord record = args.Event;
        switch (record.Kind)
        {
            case DebuggerEventKind.CreateProcess:
                Publish("debugger.processCreated", CreateEventPayload(record));
                break;
            case DebuggerEventKind.ExitProcess:
                Publish("debugger.processExited", CreateEventPayload(record));
                break;
            case DebuggerEventKind.CreateThread:
                Publish("debugger.threadCreated", CreateEventPayload(record));
                break;
            case DebuggerEventKind.ExitThread:
                Publish("debugger.threadExited", CreateEventPayload(record));
                break;
            case DebuggerEventKind.LoadModule:
                Publish("modules.changed", CreateEventPayload(record));
                break;
            case DebuggerEventKind.UnloadModule:
                Publish("modules.changed", CreateEventPayload(record));
                break;
            case DebuggerEventKind.OutputDebugString:
                Publish("debugger.output", CreateEventPayload(record));
                break;
            case DebuggerEventKind.Exception when record.ExceptionCode == BreakpointExceptionCode:
                Publish("debugger.breakpointHit", CreateEventPayload(record));
                break;
            case DebuggerEventKind.Exception:
                Publish("debugger.exception", CreateEventPayload(record));
                break;
        }

        Publish(
            "debugger.debugEvent",
            new
            {
                kind = record.Kind.ToString(),
                processId = record.ProcessId,
                threadId = record.ThreadId,
                address = FormatAddress(record.Address),
                exceptionCode = record.ExceptionCode,
                firstChance = record.FirstChance,
                message = record.Message
            });
    }

    private async ValueTask<bool> HandleBreakpointFilterRequested(
        BreakpointFilterRequestEventArgs args,
        CancellationToken cancellationToken)
    {
        PluginBreakpointFilterSubscription[] filters;
        lock (_subscriptionGate)
        {
            filters = _breakpointFilters.ToArray();
        }

        PluginBreakpointHitEvent hit = new()
        {
            Address = args.Address,
            ProcessId = args.ProcessId,
            ThreadId = args.ThreadId,
            InstructionPointer = args.InstructionPointer
        };

        foreach (PluginBreakpointFilterSubscription filter in filters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                PluginBreakpointDecision decision = await filter.Handler(hit, cancellationToken)
                    .ConfigureAwait(false);
                if (decision == PluginBreakpointDecision.Suppress)
                {
                    return true;
                }

                if (decision == PluginBreakpointDecision.Break)
                {
                    return false;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    $"Plugin breakpoint filter failed: {filter.PluginId}: {exception}");
            }
        }

        return false;
    }

    private static object CreateEventPayload(DebuggerEventRecord record) => new
    {
        processId = record.ProcessId,
        threadId = record.ThreadId,
        address = FormatAddress(record.Address),
        exceptionCode = record.ExceptionCode,
        firstChance = record.FirstChance,
        message = record.Message
    };

    private static string FormatAddress(ulong address) => $"0x{address:X}";

    private sealed class PluginContext : IPluginContext
    {
        private readonly PluginRuntime _runtime;

        internal PluginContext(
            PluginRuntime runtime,
            PluginManifest manifest,
            string pluginDirectory)
        {
            _runtime = runtime;
            Manifest = manifest;
            Logger = new PluginLogger(manifest.Name);
            Events = new PluginEventBus(runtime, manifest.Id);
            Memory = new PluginMemory(_runtime._session);
            Debugger = new PluginDebugger(_runtime._session);
            Disassembly = new PluginDisassembly(_runtime._session);
            Search = new PluginSearch(_runtime._session);
            UserInterface = new PluginUserInterface(runtime, manifest.Id);
            Storage = new PluginStorage(pluginDirectory);
            Commands = new PluginCommandBus(_runtime._mcpTools);
        }

        public PluginManifest Manifest { get; }

        public IPluginLogger Logger { get; }

        public IPluginCommandBus Commands { get; }

        public IPluginEventBus Events { get; }

        public IPluginMemory Memory { get; }

        public IPluginDebug Debugger { get; }

        public IPluginDisassembly Disassembly { get; }

        public IPluginSearch Search { get; }

        public IPluginUserInterface UserInterface { get; }

        public IPluginStorage Storage { get; }
    }

    private sealed class PluginLogger : IPluginLogger
    {
        private readonly string _pluginName;

        internal PluginLogger(string pluginName)
        {
            _pluginName = pluginName;
        }

        public void Info(string message) => Debug.WriteLine($"[Plugin:{_pluginName}] {message}");

        public void Warning(string message) => Debug.WriteLine($"[Plugin:{_pluginName}:warn] {message}");

        public void Error(string message) => Debug.WriteLine($"[Plugin:{_pluginName}:error] {message}");
    }

    private sealed class PluginEventBus : IPluginEventBus
    {
        private readonly PluginRuntime _runtime;
        private readonly string _pluginId;

        internal PluginEventBus(PluginRuntime runtime, string pluginId)
        {
            _runtime = runtime;
            _pluginId = pluginId;
        }

        public IDisposable Subscribe(
            string eventName,
            Func<PluginEvent, CancellationToken, ValueTask> handler) =>
            _runtime.Subscribe(_pluginId, eventName, handler);

        public IDisposable SubscribeBreakpointFilter(
            Func<PluginBreakpointHitEvent, CancellationToken, ValueTask<PluginBreakpointDecision>> handler) =>
            _runtime.SubscribeBreakpointFilter(_pluginId, handler);
    }

    private sealed class PluginMemory : IPluginMemory
    {
        private readonly DebuggerSession _session;

        internal PluginMemory(DebuggerSession session)
        {
            _session = session;
        }

        public ValueTask<byte[]> ReadBytesAsync(
            ulong address,
            int size,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_session.ReadBytes(address, size));
        }

        public ValueTask<bool> WriteBytesAsync(
            ulong address,
            byte[] bytes,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_session.WriteBytes(address, bytes));
        }

        public ValueTask<int?> ReadInt32Async(
            ulong address,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = _session.ReadBytes(address, sizeof(int));
            return ValueTask.FromResult(bytes.Length == sizeof(int)
                ? BitConverter.ToInt32(bytes)
                : default(int?));
        }

        public ValueTask<ulong?> ReadUInt64Async(
            ulong address,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = _session.ReadBytes(address, sizeof(ulong));
            return ValueTask.FromResult(bytes.Length == sizeof(ulong)
                ? BitConverter.ToUInt64(bytes)
                : default(ulong?));
        }
    }

    private sealed class PluginDebugger : IPluginDebug
    {
        private readonly DebuggerSession _session;

        internal PluginDebugger(DebuggerSession session)
        {
            _session = session;
        }

        public bool IsPaused => _session.Debugger.IsPaused;

        public bool IsDebugging => _session.Debugger.IsDebugging;

        public ValueTask ContinueAsync(CancellationToken cancellationToken = default) =>
            new(_session.Debugger.ContinueAsync(cancellationToken));

        public ValueTask BreakAsync(CancellationToken cancellationToken = default) =>
            new(_session.Debugger.BreakAsync(cancellationToken));

        public ValueTask StepIntoAsync(CancellationToken cancellationToken = default) =>
            new(_session.Debugger.StepIntoAsync(cancellationToken));

        public ValueTask StepOverAsync(CancellationToken cancellationToken = default) =>
            new(_session.Debugger.StepOverAsync(cancellationToken));

        public ValueTask StepOutAsync(CancellationToken cancellationToken = default) =>
            new(_session.Debugger.StepOutAsync(cancellationToken));
    }

    private sealed class PluginDisassembly : IPluginDisassembly
    {
        private readonly DebuggerSession _session;

        internal PluginDisassembly(DebuggerSession session)
        {
            _session = session;
        }

        public ValueTask<PluginDisassemblySelection> GetSelectionAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<DogeDebugger.Core.Disassembly.InstructionSnapshot> lines =
                _session.Disassemble(_session.Debugger.CurrentInstructionPointer, 1);
            return ValueTask.FromResult(new PluginDisassemblySelection
            {
                Address = lines.Count == 0 ? null : lines[0].Address,
                Lines = lines.Select(line => new PluginDisassemblyLine
                {
                    Address = line.Address,
                    Bytes = line.Bytes,
                    Text = line.Text
                }).ToArray()
            });
        }
    }

    private sealed class PluginSearch : IPluginSearch
    {
        private readonly DebuggerSession _session;

        internal PluginSearch(DebuggerSession session)
        {
            _session = session;
        }

        public ValueTask<IReadOnlyList<PluginModuleInfo>> GetModulesAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<PluginModuleInfo>>(
                _session.EnumerateModules().Select(module => new PluginModuleInfo
                {
                    Name = module.Name,
                    Path = module.FilePath,
                    BaseAddress = module.BaseAddress,
                    Size = module.Size,
                    EntryPoint = module.EntryPoint
                }).ToArray());
        }

        public ValueTask<IReadOnlyList<PluginMemoryRegionInfo>> GetMemoryRegionsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<PluginMemoryRegionInfo>>(
                _session.EnumerateMemoryRegions().Select(region => new PluginMemoryRegionInfo
                {
                    BaseAddress = region.BaseAddress,
                    Size = region.Size,
                    State = region.State.ToString(CultureInfo.InvariantCulture),
                    Protect = region.ProtectText,
                    ModuleName = region.ModuleName
                }).ToArray());
        }

        public ValueTask<string> GenerateSignatureAsync(
            PluginSignatureOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = _session.ReadBytes(
                options.Address,
                Math.Clamp(options.BeforeBytes + options.AfterBytes, 1, 4096));
            return ValueTask.FromResult(Convert.ToHexString(bytes));
        }

        public ValueTask<PluginSearchResult> RunAsync(
            PluginSearchRequest request,
            CancellationToken cancellationToken = default)
        {
            DogeDebugger.Core.Search.SearchResult result = _session.Search(
                new DogeDebugger.Core.Search.SearchRequest
                {
                    Pattern = request.Pattern,
                    StartAddress = request.StartAddress,
                    EndAddress = request.EndAddress,
                    MaximumResults = request.MaxResults
                },
                cancellationToken);
            return ValueTask.FromResult(new PluginSearchResult
            {
                Truncated = result.Truncated,
                Matches = result.Matches.Select(match => new PluginSearchMatch
                {
                    Address = match.Address,
                    Bytes = match.Bytes
                }).ToArray()
            });
        }
    }

    private sealed class PluginUserInterface : IPluginUserInterface
    {
        private readonly PluginRuntime _runtime;
        private readonly string _pluginId;

        internal PluginUserInterface(PluginRuntime runtime, string pluginId)
        {
            _runtime = runtime;
            _pluginId = pluginId;
        }

        public void RegisterPanel(
            string id,
            string title,
            Func<FrameworkElement> panelFactory) =>
            _runtime.OnPanelRegistered(
                new PluginPanelRegistration(_pluginId, id, title, panelFactory));

        public void RegisterMenu(
            string header,
            Action<IPluginMenuBuilder> configure) =>
            RegisterMenuCore(PluginContextMenuTarget.Disassembly, null, header, configure, isMainMenu: true);

        public void RegisterContextMenu(
            PluginContextMenuTarget target,
            string header,
            Action<IPluginMenuBuilder> configure) =>
            RegisterMenuCore(target, target, header, configure, isMainMenu: false);

        public bool RegisterHotkey(
            string id,
            string displayName,
            PluginKeyGesture gesture,
            Func<CancellationToken, ValueTask> handler)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            ArgumentNullException.ThrowIfNull(gesture);
            ArgumentNullException.ThrowIfNull(handler);

            PluginHotkeyRegistration registration = new(
                _pluginId,
                id,
                displayName,
                gesture,
                handler);
            lock (_runtime._subscriptionGate)
            {
                if (_runtime._hotkeys.ContainsKey(registration.HotkeyKey))
                {
                    return false;
                }

                _runtime._hotkeys.Add(registration.HotkeyKey, registration);
            }

            _runtime.OnHotkeyRegistered(registration);
            return true;
        }

        public void UnregisterHotkey(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            string key = PluginHotkeyRegistration.CreateKey(_pluginId, id);
            lock (_runtime._subscriptionGate)
            {
                if (!_runtime._hotkeys.Remove(key))
                {
                    return;
                }
            }

            _runtime.OnHotkeyUnregistered(_pluginId, id);
        }

        public void OpenPanel(string id) =>
            _runtime.OnPanelOpenRequested(_pluginId, id);

        public void NavigateToDisassembly(ulong address) =>
            _runtime.OnDisassemblyNavigationRequested(_pluginId, address);

        public void NavigateToHex(ulong address) =>
            _runtime.OnHexNavigationRequested(_pluginId, address);

        private void RegisterMenuCore(
            PluginContextMenuTarget target,
            PluginContextMenuTarget? contextTarget,
            string header,
            Action<IPluginMenuBuilder> configure,
            bool isMainMenu)
        {
            PluginMenuBuilder builder = new();
            configure(builder);
            _runtime.OnMenuRegistered(new PluginMenuRegistration(
                _pluginId,
                isMainMenu,
                contextTarget,
                header,
                builder.Items));
        }
    }

    private sealed class PluginMenuBuilder : IPluginMenuBuilder
    {
        private readonly List<PluginMenuItem> _items = [];

        internal IReadOnlyList<PluginMenuItem> Items => _items;

        public void AddItem(string header, Func<CancellationToken, ValueTask> handler) =>
            AddItem(header, null, handler);

        public void AddItem(
            string header,
            string? hotkeyId,
            Func<CancellationToken, ValueTask> handler)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(header);
            _items.Add(new PluginMenuItem
            {
                Header = header,
                HotkeyId = hotkeyId,
                Handler = handler
            });
        }

        public void AddSubMenu(string header, Action<IPluginMenuBuilder> configure)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(header);
            PluginMenuBuilder child = new();
            configure(child);
            _items.Add(new PluginMenuItem
            {
                Header = header,
                Items = child.Items
            });
        }

        public void AddSeparator() =>
            _items.Add(new PluginMenuItem { IsSeparator = true });
    }

    private sealed class PluginStorage : IPluginStorage
    {
        private readonly string _path;

        internal PluginStorage(string pluginDirectory)
        {
            Directory = Path.Combine(pluginDirectory, "data");
            _path = Path.Combine(Directory, "settings.json");
        }

        public string Directory { get; }

        public async ValueTask<string?> GetJsonAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            Dictionary<string, JsonElement> values = await ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            return values.TryGetValue(key, out JsonElement value)
                ? value.GetRawText()
                : null;
        }

        public async ValueTask SetJsonAsync(
            string key,
            string json,
            CancellationToken cancellationToken = default)
        {
            Dictionary<string, JsonElement> values = await ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(json);
            values[key] = document.RootElement.Clone();
            await WriteAsync(values, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask RemoveAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            Dictionary<string, JsonElement> values = await ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (values.Remove(key))
            {
                await WriteAsync(values, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<Dictionary<string, JsonElement>> ReadAsync(
            CancellationToken cancellationToken)
        {
            if (!File.Exists(_path))
            {
                return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            }

            try
            {
                await using FileStream stream = File.OpenRead(_path);
                return await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(
                           stream,
                           cancellationToken: cancellationToken)
                           .ConfigureAwait(false) ??
                       new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            }
            catch (JsonException)
            {
                return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            }
        }

        private async Task WriteAsync(
            Dictionary<string, JsonElement> values,
            CancellationToken cancellationToken)
        {
            System.IO.Directory.CreateDirectory(Directory);
            await using FileStream stream = File.Create(_path);
            await JsonSerializer.SerializeAsync(
                    stream,
                    values,
                    new JsonSerializerOptions { WriteIndented = true },
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed class PluginCommandBus : IPluginCommandBus
    {
        private readonly McpToolRegistry? _registry;

        internal PluginCommandBus(McpToolRegistry? registry)
        {
            _registry = registry;
        }

        public async ValueTask<string> CallAsync(
            string name,
            string argumentsJson = "{}",
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (_registry is null ||
                !_registry.TryGet(name, out McpToolDefinition? definition) ||
                definition is null)
            {
                throw new KeyNotFoundException($"MCP command '{name}' is not registered.");
            }

            using JsonDocument arguments = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            McpToolResult result = await definition.Handler(arguments.RootElement, cancellationToken)
                .ConfigureAwait(false);
            if (result.StructuredContent is { } structuredContent)
            {
                return structuredContent.GetRawText();
            }

            return JsonSerializer.Serialize(new
            {
                isError = result.IsError,
                text = result.Text
            });
        }

        public IReadOnlyList<PluginCommandDescriptor> ListTools() =>
            _registry?.Tools.Select(tool => new PluginCommandDescriptor
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = tool.InputSchema
            }).ToArray() ?? [];
    }

    private sealed class PluginSubscription : IDisposable
    {
        private Action? _dispose;

        internal PluginSubscription(Action dispose)
        {
            _dispose = dispose;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _dispose, null)?.Invoke();
        }
    }

    private sealed record PluginEventSubscription(
        string PluginId,
        string EventName,
        Func<PluginEvent, CancellationToken, ValueTask> Handler);

    private sealed record PluginBreakpointFilterSubscription(
        string PluginId,
        Func<PluginBreakpointHitEvent, CancellationToken, ValueTask<PluginBreakpointDecision>>
            Handler);
}

public sealed class LoadedPlugin : IAsyncDisposable
{
    private readonly AssemblyLoadContext _loadContext;
    private readonly IPlugin _plugin;
    private bool _disposed;

    internal LoadedPlugin(
        PluginDescriptor descriptor,
        AssemblyLoadContext loadContext,
        IPlugin plugin)
    {
        Descriptor = descriptor;
        _loadContext = loadContext;
        _plugin = plugin;
    }

    public PluginDescriptor Descriptor { get; }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_plugin is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else if (_plugin is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _loadContext.Unload();
        _disposed = true;
    }
}

public sealed record PluginPanelRegistration(
    string PluginId,
    string Id,
    string Title,
    Func<FrameworkElement> Factory)
{
    public string ContentId => CreateContentId(PluginId, Id);

    public static string CreateContentId(string pluginId, string panelId) =>
        $"Plugin.{pluginId}.{panelId}";
}

public sealed record PluginMenuRegistration(
    string PluginId,
    bool IsMainMenu,
    PluginContextMenuTarget? ContextTarget,
    string Header,
    IReadOnlyList<PluginMenuItem> Items);

public sealed record PluginHotkeyRegistration(
    string PluginId,
    string Id,
    string DisplayName,
    PluginKeyGesture Gesture,
    Func<CancellationToken, ValueTask> Handler)
{
    public string HotkeyKey => CreateKey(PluginId, Id);

    public static string CreateKey(string pluginId, string id) =>
        $"{pluginId}\u001f{id}";
}
