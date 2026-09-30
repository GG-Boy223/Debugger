using System.Text;
using System.Text.Json;
using System.IO;
using DogeDebugger.PluginSdk;

namespace DogeDebugger.Debugger.Plugins;

public sealed class PluginManifestReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public PluginDescriptor Read(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        manifestPath = Path.GetFullPath(manifestPath);
        string directory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidDataException("Plugin manifest has no parent directory.");
        string json = File.ReadAllText(manifestPath, Encoding.UTF8);
        PluginManifest manifest = JsonSerializer.Deserialize<PluginManifest>(json, JsonOptions)
            ?? throw new InvalidDataException("plugin.json is empty or invalid.");

        ValidateManifest(manifest);
        string entryPath = Path.GetFullPath(Path.Combine(directory, manifest.Entry));
        if (!IsWithinDirectory(directory, entryPath))
        {
            throw new InvalidDataException("Plugin entry must remain inside the plugin directory.");
        }

        if (!File.Exists(entryPath))
        {
            throw new FileNotFoundException("Plugin entry was not found.", entryPath);
        }

        return new PluginDescriptor(
            manifest,
            directory,
            entryPath,
            manifest.Type.Equals("web", StringComparison.OrdinalIgnoreCase)
                ? PluginPackageKind.Web
                : PluginPackageKind.Managed);
    }

    private static void ValidateManifest(PluginManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id) ||
            string.IsNullOrWhiteSpace(manifest.Type) ||
            string.IsNullOrWhiteSpace(manifest.Name) ||
            string.IsNullOrWhiteSpace(manifest.Version) ||
            string.IsNullOrWhiteSpace(manifest.Description) ||
            string.IsNullOrWhiteSpace(manifest.Entry))
        {
            throw new InvalidDataException("plugin.json is missing required fields.");
        }

        if (!manifest.Type.Equals("csharp", StringComparison.OrdinalIgnoreCase) &&
            !manifest.Type.Equals("web", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unsupported plugin type: {manifest.Type}");
        }
    }

    private static bool IsWithinDirectory(string directory, string path)
    {
        string relative = Path.GetRelativePath(directory, path);
        return !relative.StartsWith("..", StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }
}

public enum PluginPackageKind
{
    Managed,
    Web
}

public sealed record PluginDescriptor(
    PluginManifest Manifest,
    string Directory,
    string EntryPath,
    PluginPackageKind Kind);
