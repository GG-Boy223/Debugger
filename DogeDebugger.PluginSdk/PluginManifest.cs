using System.Text.Json.Serialization;

namespace DogeDebugger.PluginSdk;

public sealed class PluginManifest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "csharp";

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("entry")]
    public string Entry { get; set; } = string.Empty;

    [JsonPropertyName("pluginClass")]
    public string? PluginClass { get; set; }
}
