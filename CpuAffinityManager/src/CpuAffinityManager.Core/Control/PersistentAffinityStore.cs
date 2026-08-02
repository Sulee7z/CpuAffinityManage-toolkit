using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using CpuAffinityManager.Engine;

namespace CpuAffinityManager.ProcOps;

/// <summary>One persisted manual-affinity choice, keyed by executable name.</summary>
public sealed class PersistentAffinityEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";      // e.g. "game.exe" (lower-case)
    [JsonPropertyName("mask")] public ulong Mask { get; set; }
    [JsonPropertyName("hardLock")] public bool HardLock { get; set; }
}

/// <summary>
/// Persists manual "优先跑指定核心" choices across app restarts. Because PIDs change
/// between runs, entries are keyed by executable NAME, so the choice is re-applied to any
/// running (or newly launched) instance of that program. Stored under
/// %LOCALAPPDATA%\CpuAffinityManager\manual-affinity.json — the same writable location as
/// the rules/AI config. The affinity watchdog reads this every tick to keep the mask.
/// </summary>
public static class PersistentAffinityStore
{
    private static readonly ConcurrentDictionary<string, PersistentAffinityEntry> _map =
        new(StringComparer.OrdinalIgnoreCase);
    // PIDs already hard-locked this session, so we don't re-create a Job every tick.
    private static readonly ConcurrentDictionary<int, byte> _hardLocked = new();
    private static bool _loaded;

    private static string FilePath =>
        System.IO.Path.Combine(RuleConfigPath.DataDirectory, "manual-affinity.json");

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (System.IO.File.Exists(FilePath))
            {
                string json = System.IO.File.ReadAllText(FilePath);
                var list = JsonSerializer.Deserialize(json, PersistentAffinityJsonContext.Default.ListPersistentAffinityEntry);
                if (list != null)
                    foreach (var e in list)
                        if (!string.IsNullOrWhiteSpace(e.Name) && e.Mask != 0)
                            _map[e.Name] = e;
            }
        }
        catch { }
    }

    /// <summary>Adds/updates a persisted entry for a process name and saves to disk.</summary>
    public static void Upsert(string exeName, ulong mask, bool hardLock)
    {
        if (string.IsNullOrWhiteSpace(exeName) || mask == 0) return;
        EnsureLoaded();
        _map[exeName] = new PersistentAffinityEntry { Name = exeName, Mask = mask, HardLock = hardLock };
        Save();
    }

    /// <summary>Removes a persisted entry (called when a program's affinity is reset / persist un-checked).</summary>
    public static void Remove(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return;
        EnsureLoaded();
        if (_map.TryRemove(exeName, out _)) Save();
    }

    public static bool TryGet(string exeName, out PersistentAffinityEntry entry)
    {
        EnsureLoaded();
        return _map.TryGetValue(exeName, out entry!);
    }

    public static bool IsEmpty { get { EnsureLoaded(); return _map.IsEmpty; } }

    // ── one-time hard-lock bookkeeping ──
    public static bool AlreadyHardLocked(int pid) => _hardLocked.ContainsKey(pid);
    public static void MarkHardLocked(int pid) => _hardLocked[pid] = 1;

    /// <summary>
    /// Drops hard-lock markers for PIDs that are no longer running, so a recycled
    /// PID is not wrongly treated as already hard-locked and skipped by the watchdog.
    /// </summary>
    public static void PruneHardLocked(ISet<int> livePids)
    {
        foreach (var pid in _hardLocked.Keys)
            if (!livePids.Contains(pid))
                _hardLocked.TryRemove(pid, out _);
    }

    private static void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(RuleConfigPath.DataDirectory);
            var list = new List<PersistentAffinityEntry>(_map.Values);
            string json = JsonSerializer.Serialize(list, PersistentAffinityJsonContext.Default.ListPersistentAffinityEntry);
            System.IO.File.WriteAllText(FilePath, json);
        }
        catch { }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<PersistentAffinityEntry>))]
public partial class PersistentAffinityJsonContext : JsonSerializerContext
{
}
