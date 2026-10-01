namespace DogeDebugger.Core.Shortcuts;

[Flags]
public enum ShortcutActionFlags
{
    None = 0,
    NoRepeat = 1
}

public enum ShortcutCategory
{
    Debug,
    File,
    Trace,
    View,
    Panel,
    Tools,
    Disasm,
    DisasmBookmarks,
    Hex,
    Registers,
    CallStack,
    AddressList,
    MemorySearch,
    Lua,
    Breakpoints,
    Lists
}

public enum ShortcutScope
{
    Application,
    System,
    View
}

public sealed class ShortcutActionDescriptor
{
    public ShortcutActionDescriptor(
        string id,
        ShortcutCategory category,
        string name,
        string description,
        ShortcutScope scope = ShortcutScope.Application,
        string? viewContext = null,
        KeyChord? defaultChord = null,
        ShortcutActionFlags flags = ShortcutActionFlags.None)
    {
        Id = id;
        Category = category;
        Name = name;
        Description = description;
        Scope = scope;
        ViewContext = viewContext;
        DefaultChord = defaultChord;
        Flags = flags;
    }

    public string Id { get; }

    public ShortcutCategory Category { get; }

    public string Name { get; }

    public string Description { get; }

    public ShortcutScope Scope { get; }

    public string? ViewContext { get; }

    public KeyChord? DefaultChord { get; }

    public ShortcutActionFlags Flags { get; }

    public bool Has(ShortcutActionFlags flag) => (Flags & flag) != 0;
}

public sealed class ShortcutScheme
{
    public ShortcutScheme(
        string id,
        string name,
        bool isBuiltIn,
        string? baseSchemeId,
        IReadOnlyDictionary<string, KeyChord?> delta,
        string? description = null)
    {
        Id = id;
        Name = name;
        IsBuiltIn = isBuiltIn;
        BaseSchemeId = baseSchemeId;
        Delta = new Dictionary<string, KeyChord?>(delta, StringComparer.OrdinalIgnoreCase);
        Description = description ?? string.Empty;
    }

    public string Id { get; }

    public string Name { get; }

    public bool IsBuiltIn { get; }

    public string? BaseSchemeId { get; }

    public IReadOnlyDictionary<string, KeyChord?> Delta { get; }

    public string Description { get; }
}

public sealed class ShortcutSettings
{
    public int Version { get; set; } = 1;

    public string ActiveScheme { get; set; } = "x64dbg";

    public Dictionary<string, KeyChord?> Overrides { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public ShortcutSettings Clone()
    {
        return new ShortcutSettings
        {
            Version = Version,
            ActiveScheme = ActiveScheme,
            Overrides = new Dictionary<string, KeyChord?>(
                Overrides,
                StringComparer.OrdinalIgnoreCase)
        };
    }
}
