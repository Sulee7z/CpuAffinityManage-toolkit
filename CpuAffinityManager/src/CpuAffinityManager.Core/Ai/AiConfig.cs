using System.Text.Json;
using System.Text.Json.Serialization;
using CpuAffinityManager.Engine;

namespace CpuAffinityManager.Ai;

/// <summary>
/// Persisted configuration for the third-party AI provider (OpenAI-compatible).
/// Stored per-user under %LOCALAPPDATA%\CpuAffinityManager\ai-config.json — the same
/// writable location as the rules file, so it survives restarts and never touches the
/// install directory. The API key is the user's own credential and is stored locally.
/// </summary>
public class AiConfig
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "OpenAI";

    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "gpt-4o-mini";

    private static string FilePath =>
        System.IO.Path.Combine(RuleConfigPath.DataDirectory, "ai-config.json");

    public static AiConfig Load()
    {
        try
        {
            if (System.IO.File.Exists(FilePath))
            {
                string json = System.IO.File.ReadAllText(FilePath);
                var cfg = JsonSerializer.Deserialize(json, AiConfigJsonContext.Default.AiConfig);
                if (cfg != null) return cfg;
            }
        }
        catch { }
        return new AiConfig();
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(RuleConfigPath.DataDirectory);
            string json = JsonSerializer.Serialize(this, AiConfigJsonContext.Default.AiConfig);
            System.IO.File.WriteAllText(FilePath, json);
        }
        catch { }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AiConfig))]
public partial class AiConfigJsonContext : JsonSerializerContext
{
}
