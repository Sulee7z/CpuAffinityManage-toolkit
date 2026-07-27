using System.Runtime;
using System.Runtime.Versioning;

namespace CpuAffinityManager.ProcOps;

/// <summary>
/// Keeps THIS app's own memory footprint minimal: every interval it runs a compacting
/// background-friendly GC, releases large-object-heap fragmentation, and trims the
/// process working set back to the OS (the same EmptyWorkingSet used by the "释放物理
/// 内存" feature — applied to ourselves). Cheap: one timer tick per minute.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SelfMemoryTrimmer
{
    private static Timer? _timer;

    /// <summary>Starts periodic self-trimming (default every 60 s; first trim after 15 s).</summary>
    public static void Start(int intervalSeconds = 60)
    {
        if (_timer != null) return;
        _timer = new Timer(_ => Trim(), null,
            TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(Math.Max(20, intervalSeconds)));
    }

    public static void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>One trim pass: compact the managed heap, then return freed pages to the OS.</summary>
    public static void Trim()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: true);
            ProcessControlService.EmptyWorkingSet(Environment.ProcessId);
        }
        catch { /* best-effort */ }
    }
}
