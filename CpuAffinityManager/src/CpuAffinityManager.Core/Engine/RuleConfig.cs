using System.Text.Json;
using System.Text.Json.Serialization;

namespace CpuAffinityManager.Engine;

/// <summary>
/// Root configuration object for JSON serialization/deserialization
/// of the rule set and global settings. Uses System.Text.Json source generation
/// for AOT/NativeAOT compatibility.
/// </summary>
public class RuleConfig
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 2;

    [JsonPropertyName("rules")]
    public List<RuleEntry> Rules { get; set; } = new();

    [JsonPropertyName("settings")]
    public AppSettings Settings { get; set; } = new();

    /// <summary>
    /// Loads rules from a JSON file (AOT-safe, uses source gen).
    /// </summary>
    public static RuleConfig Load(string filePath)
    {
        if (!File.Exists(filePath))
            return new RuleConfig();

        string json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize(json, RuleConfigJsonContext.Default.RuleConfig) ?? new RuleConfig();
    }

    /// <summary>
    /// Saves rules to a JSON file with indentation (AOT-safe, uses source gen).
    /// </summary>
    public void Save(string filePath)
    {
        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string json = JsonSerializer.Serialize(this, RuleConfigJsonContext.Default.RuleConfig);
        File.WriteAllText(filePath, json);
    }
}

/// <summary>
/// Global application settings stored in the config file.
/// </summary>
public class AppSettings
{
    [JsonPropertyName("enableWmiMonitor")]
    public bool EnableWmiMonitor { get; set; } = true;

    [JsonPropertyName("confirmBeforeApply")]
    public bool ConfirmBeforeApply { get; set; } = false;

    [JsonPropertyName("minimizeToTray")]
    public bool MinimizeToTray { get; set; } = true;
}

/// <summary>
/// Source-generated JSON serialization context for AOT compatibility.
/// Registers all types that participate in JSON serialization.
/// </summary>
[JsonSerializable(typeof(RuleConfig))]
[JsonSerializable(typeof(RuleEntry))]
[JsonSerializable(typeof(RuleMatch))]
[JsonSerializable(typeof(RuleAction))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<RuleEntry>))]
[JsonSerializable(typeof(List<string>))]
public partial class RuleConfigJsonContext : JsonSerializerContext
{
}
