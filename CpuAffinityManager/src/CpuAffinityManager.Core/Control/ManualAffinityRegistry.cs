using System.Collections.Concurrent;

namespace CpuAffinityManager.ProcOps;

/// <summary>
/// Records per-process MANUAL CPU-affinity choices (from the "选择核心" dialog) so the
/// affinity watchdog can treat them as first-class, protecting them the same way it
/// protects rule-based affinity. <see cref="ManualWins"/> decides who wins when a manual
/// choice and an enforcement rule both target the same process:
///   • true  (default) — 指定核心优先:the watchdog re-asserts the manual mask and skips the rule.
///   • false           — 规则优先:the watchdog ignores manual entries and enforces rules as usual.
/// In-memory only (per session); the default already matches "manual wins".
/// </summary>
public static class ManualAffinityRegistry
{
    private static readonly ConcurrentDictionary<int, ulong> _map = new();

    /// <summary>true = 指定核心优先级高(手动 &gt; 规则);false = 规则优先级高。</summary>
    public static volatile bool ManualWins = true;

    public static void Set(int pid, ulong mask) => _map[pid] = mask;
    public static void Remove(int pid) => _map.TryRemove(pid, out _);
    public static bool TryGet(int pid, out ulong mask) => _map.TryGetValue(pid, out mask);
    public static bool Has(int pid) => _map.ContainsKey(pid);
    public static bool IsEmpty => _map.IsEmpty;
    public static void Clear() => _map.Clear();
}
