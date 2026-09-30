using DogeDebugger.Plugins.UnrealEngine.Models;

namespace DogeDebugger.Plugins.UnrealEngine.Services;

internal sealed class UnrealOffsetCacheDocument
{
    public int SchemaVersion { get; set; } = 1;

    public string ModulePath { get; set; } = string.Empty;

    public ulong ModuleSize { get; set; }

    public string ImageHash { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    public UnrealOffsets Offsets { get; set; } = new();

    public uint? NamePoolRva { get; set; }

    public uint? NamePoolBlockArrayRva { get; set; }

    public uint? GObjectsRva { get; set; }

    public uint? GWorldRva { get; set; }

    public uint? GEngineRva { get; set; }
}
