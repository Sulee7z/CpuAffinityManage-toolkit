using System.Diagnostics;
using System.Runtime.Versioning;
using CpuAffinityManager.Cpu;
using CpuAffinityManager.Engine;
using CpuAffinityManager.ProcOps;
using Serilog;

namespace CpuAffinityManager.Enforcement;

/// <summary>
/// Re-applies affinity rules when a running process resets its own affinity.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AffinityEnforcementWatchdog : IDisposable
{
    private readonly IRuleEngine _ruleEngine;
    private readonly ICpuTopologyService _topologyService;
    private readonly IEnforcementService _enforcementService;
    private readonly TimeSpan _period;
    private Timer? _timer;
    private int _isTicking;

    public AffinityEnforcementWatchdog(
        IRuleEngine ruleEngine,
        ICpuTopologyService topologyService,
        IEnforcementService enforcementService,
        TimeSpan? period = null)
    {
        _ruleEngine = ruleEngine;
        _topologyService = topologyService;
        _enforcementService = enforcementService;
        // 1s is responsive enough to re-clamp a process that reset its own affinity,
        // while cutting the background scan rate 4x versus the old 250ms loop.
        _period = period ?? TimeSpan.FromMilliseconds(1000);
    }

    public void Start()
    {
        _timer ??= new Timer(Tick, null, _period, _period);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public int EnforceOnce()
    {
        int changed = 0;

        // ── 内存/CPU 优化:没活干就不扫描 ──
        // 只有存在需要持续强制的规则、或手动/持久化核心记录时才枚举全部进程;
        // 否则每秒 Process.GetProcesses() 会白白产生大量临时对象(GC 压力)。
        bool anyOngoingRule = false;
        foreach (var r in _ruleEngine.Rules)
        {
            if (r.Enabled && r.Action != null &&
                (r.Action.Level is "hard-affinity" or "job-enforced" or "job-locked"
                 || r.Action.GetPreferredMask() != 0))
            { anyOngoingRule = true; break; }
        }
        bool anyManual = ManualAffinityRegistry.ManualWins &&
                         (!ManualAffinityRegistry.IsEmpty || !PersistentAffinityStore.IsEmpty);
        if (!anyOngoingRule && !anyManual)
            return 0;

        CpuTopology topology = _topologyService.Detect();

        // Resolve the executable path only if at least one enabled rule actually
        // uses a path condition. When no rule does, we skip the per-process native
        // path query entirely — the common case for name-only rule sets.
        bool needPath = false;
        foreach (var r in _ruleEngine.Rules)
        {
            if (r.Enabled && !string.IsNullOrEmpty(r.Match.Path)) { needPath = true; break; }
        }

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                int pid = process.Id;
                if (pid is 0 or 4)
                    continue;

                // 指定核心优先:若该进程有手动选择的核心且“指定核心优先级高”开启,
                // 则守护线程负责把它维持在手动掩码上,并跳过规则(手动 > 规则)。
                if (ManualAffinityRegistry.ManualWins &&
                    ManualAffinityRegistry.TryGet(pid, out ulong manualMask) && manualMask != 0)
                {
                    ulong want = CpuTopology.ClampToLogicalProcessors(manualMask, topology.TotalLogicalProcessors);
                    ulong have = CpuTopology.ClampToLogicalProcessors(
                        (ulong)process.ProcessorAffinity.ToInt64(), topology.TotalLogicalProcessors);
                    if (want != 0 && have != want)
                    {
                        ProcessControlService.SetAffinityMask(pid, want);
                        changed++;
                        Log.Debug("Re-asserted MANUAL affinity to PID {Pid}: 0x{Have:X} -> 0x{Want:X}", pid, have, want);
                    }
                    continue; // 手动优先,忽略规则
                }

                string name = process.ProcessName + ".exe";

                // 重启后保留:按“程序名”记住的手动核心设置,重启软件/新开实例后仍生效。
                if (ManualAffinityRegistry.ManualWins &&
                    PersistentAffinityStore.TryGet(name, out var saved) && saved.Mask != 0)
                {
                    ulong want = CpuTopology.ClampToLogicalProcessors(saved.Mask, topology.TotalLogicalProcessors);
                    ulong have = CpuTopology.ClampToLogicalProcessors(
                        (ulong)process.ProcessorAffinity.ToInt64(), topology.TotalLogicalProcessors);
                    if (want != 0)
                    {
                        if (saved.HardLock && !PersistentAffinityStore.AlreadyHardLocked(pid))
                        {
                            ProcessControlService.LockAffinityMask(pid, want);
                            PersistentAffinityStore.MarkHardLocked(pid);
                            changed++;
                        }
                        else if (have != want)
                        {
                            ProcessControlService.SetAffinityMask(pid, want);
                            changed++;
                        }
                    }
                    continue; // 记住的手动设置同样优先于规则
                }

                // Fast native path query (QueryFullProcessImageName) instead of
                // Process.MainModule, which is slow and throws for most protected
                // processes. Only queried when a path-conditioned rule exists.
                string path = needPath ? (EnforcementService.GetProcessPath(pid) ?? string.Empty) : string.Empty;

                RuleEntry? rule = _ruleEngine.Match(name, path);
                if (rule?.Action == null || !RequiresOngoingEnforcement(rule))
                    continue;

                // “优先调度核心”每个周期重设一次:覆盖进程后创建的新线程,
                // 也纠正游戏自己改回的理想处理器 —— 这是它真正“生效”的关键。
                ulong prefer = rule.Action.GetPreferredMask();
                if (prefer != 0)
<<<<<<< HEAD
                    ProcOps.ProcessControlService.ApplyPreferredCores(pid, prefer, rule.Action.GetPreferMode());

                ulong expected = CpuTopology.BuildMask(topology, rule.Action.Mode, rule.Action.GetCustomMask());
                string wpm = rule.Action.GetPreferMode();
                if (wpm is "static" or "d2")
                {
                    ulong poolMask = rule.Action.GetSchedulingPoolMask();
                    if (poolMask != 0)
                        expected &= poolMask;
                }
=======
                    ProcOps.ProcessControlService.ApplyPreferredCores(pid, prefer);

                ulong expected = CpuTopology.BuildMask(topology, rule.Action.Mode, rule.Action.GetCustomMask());
>>>>>>> 07cba14d22092822ae57767f12fbf81c1eb1cba7
                if (expected == 0)
                    continue;

                ulong current = CpuTopology.ClampToLogicalProcessors(
                    (ulong)process.ProcessorAffinity.ToInt64(),
                    topology.TotalLogicalProcessors);

                if (current == expected)
                    continue;

                if (_enforcementService.Apply(pid, rule, topology))
                {
                    changed++;
                    Log.Debug(
                        "Re-applied affinity rule '{Rule}' to {Process} PID {Pid}: 0x{Current:X} -> 0x{Expected:X}",
                        rule.Name, name, pid, current, expected);
                }
            }
            catch
            {
                // Process may exit or deny access while scanning.
            }
            finally
            {
                try { process.Dispose(); } catch { }
            }
        }

        return changed;
    }

    private void Tick(object? state)
    {
        if (Interlocked.Exchange(ref _isTicking, 1) == 1)
            return;

        try
        {
            EnforceOnce();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Affinity watchdog tick failed");
        }
        finally
        {
            Volatile.Write(ref _isTicking, 0);
        }
    }

    private static bool RequiresOngoingEnforcement(RuleEntry rule)
    {
        return rule.Action.Level is "hard-affinity" or "job-enforced" or "job-locked"
            || rule.Action.GetPreferredMask() != 0;
    }

    public void Dispose() => Stop();
}
