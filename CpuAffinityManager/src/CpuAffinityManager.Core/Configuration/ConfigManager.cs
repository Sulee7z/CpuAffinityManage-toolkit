using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace CpuAffinityManager.Configuration;

public class ConfigManager
{
    private readonly string _configPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private JObject _cache = new();

    public ConfigManager(string? configDirectory = null)
    {
        configDirectory ??= Engine.RuleConfigPath.DataDirectory;
        _configPath = Path.Combine(configDirectory, "appsettings.json");
    }

    public async Task InitializeAsync()
    {
        _cache = await LoadConfigAsync().ConfigureAwait(false);
    }

    public async Task EnsureDefaultsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var defaults = new AppConfigSettings();
            var addedKeys = new List<string>();

            var properties = typeof(AppConfigSettings).GetProperties();
            foreach (var prop in properties)
            {
                var defValue = prop.GetValue(defaults);
                if (defValue == null) continue;

                if (IsNestedObject(prop.PropertyType))
                {
                    var nestedProps = prop.PropertyType.GetProperties();
                    foreach (var nestedProp in nestedProps)
                    {
                        var key = $"{prop.Name}:{nestedProp.Name}";
                        var value = nestedProp.GetValue(defValue);
                        if (value == null) continue;

                        if (GetTokenIgnoreCase(_cache, key) == null
                            && !CheckNestedExists(_cache, prop.Name, nestedProp.Name))
                        {
                            EnsureNestedValue(_cache, prop.Name, nestedProp.Name, value.ToString()!);
                            addedKeys.Add($"{key} = {value}");
                        }
                    }
                }
                else
                {
                    var key = prop.Name;
                    if (GetTokenIgnoreCase(_cache, key) == null)
                    {
                        SetValueIgnoreCase(_cache, key, defValue.ToString()!);
                        addedKeys.Add($"{key} = {defValue}");
                    }
                }
            }

            if (addedKeys.Count > 0)
            {
                await SaveConfigAsync().ConfigureAwait(false);
                Log.Information("Added {Count} missing config keys: {Keys}", addedKeys.Count, string.Join(", ", addedKeys));
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string?> GetAsync(string key)
    {
        await _lock.WaitAsync();
        try
        {
            return GetTokenIgnoreCase(_cache, key)?.Value<string>();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetAsync(string key, string value)
    {
        await _lock.WaitAsync();
        try
        {
            SetValueIgnoreCase(_cache, key, value);
            await SaveConfigAsync().ConfigureAwait(false);
            Log.Information("Config key {Key} set to {Value}", key, value);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<JObject> LoadConfigAsync()
    {
        try
        {
            var configDir = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(configDir))
                Directory.CreateDirectory(configDir);

            if (!File.Exists(_configPath))
            {
                Log.Information("Config file not found, starting with empty config");
                return new JObject();
            }

            var content = await File.ReadAllTextAsync(_configPath).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(content))
            {
                Log.Information("Config file is empty");
                return new JObject();
            }

            var config = JsonConvert.DeserializeObject<JObject>(content);
            return config ?? new JObject();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Invalid config file, resetting");
            return new JObject();
        }
    }

    private async Task SaveConfigAsync()
    {
        var json = _cache.ToString(Formatting.Indented);
        await File.WriteAllTextAsync(_configPath, json).ConfigureAwait(false);
    }

    private static bool IsNestedObject(Type type)
        => type.IsClass && !type.IsPrimitive && type != typeof(string);

    private static JToken? GetTokenIgnoreCase(JObject obj, string key)
    {
        var prop = obj.Properties()
            .FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
        return prop?.Value;
    }

    private static bool CheckNestedExists(JObject root, string section, string key)
    {
        var sectionToken = GetTokenIgnoreCase(root, section);
        if (sectionToken is not JObject sectionObj)
            return false;
        return GetTokenIgnoreCase(sectionObj, key) != null;
    }

    private static void EnsureNestedValue(JObject root, string section, string key, string value)
    {
        var sectionObj = GetOrCreateObjectIgnoreCase(root, section);
        SetValueIgnoreCase(sectionObj, key, value);
    }

    private static JObject GetOrCreateObjectIgnoreCase(JObject current, string key)
    {
        JProperty? first = null;
        foreach (var p in current.Properties())
        {
            if (string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                first = p;
                break;
            }
        }

        if (first?.Value is JObject existing)
            return existing;

        var created = new JObject();
        if (first != null)
        {
            first.Value = created;
        }
        else
        {
            current[key] = created;
        }
        return created;
    }

    private static void SetValueIgnoreCase(JObject current, string key, string value)
    {
        var toRemove = current.Properties()
            .Where(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .ToList();

        foreach (var name in toRemove)
            current.Remove(name);

        current[key] = value;
    }

    public static void ValidateConfig(string configDirectory)
    {
        var configPath = Path.Combine(configDirectory, "appsettings.json");

        try
        {
            if (!File.Exists(configPath))
            {
                File.WriteAllText(configPath, "{}");
                Log.Information("Created default empty config file: {Path}", configPath);
                return;
            }

            var content = File.ReadAllText(configPath);
            if (string.IsNullOrWhiteSpace(content))
            {
                File.WriteAllText(configPath, "{}");
                Log.Warning("Config file was empty, reset: {Path}", configPath);
                return;
            }

            var trimmed = content.Trim();
            if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}'))
            {
                File.WriteAllText(configPath, "{}");
                Log.Warning("Config file was not valid JSON, reset: {Path}", configPath);
                return;
            }

            JsonConvert.DeserializeObject<JObject>(content);
        }
        catch
        {
            File.WriteAllText(configPath, "{}");
            Log.Warning("Config file was corrupted, reset: {Path}", configPath);
        }
    }
}

public class AppConfigSettings
{
    public AppOptions App { get; set; } = new();
    public UiOptions Ui { get; set; } = new();

    public class AppOptions
    {
        public string Language { get; set; } = "zh-CN";
        public bool MinimizeToTray { get; set; } = true;
        public bool ConfirmBeforeApply { get; set; } = false;
        public bool EnableWmiMonitor { get; set; } = true;
        public bool ShowApiConsole { get; set; } = true;
    }

    public class UiOptions
    {
        public string Theme { get; set; } = "System";
        public string AccentColor { get; set; } = "#0078D4";
        public bool ShowCpuLegend { get; set; } = true;
    }
}
