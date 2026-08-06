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
        // 250ms 比旧 1s 周期更快地给新线程补上"优先核心"理想处理器提示:
        // 游戏线程每帧都会唤醒/阻塞,唤醒时调度器按理想处理器选核,
        // 周期越短,新线程被引向优先核心越及时。空闲时 EnforceOnce 直接
        // 返回,扫描开销可忽略。
        _period = period ?? TimeSpan.FromMilliseconds(250);
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
        int enabledRuleCount = 0;
        foreach (var r in _ruleEngine.Rules)
        {
            if (r.Enabled) enabledRuleCount++;
            if (r.Enabled && r.Action != null &&
                (r.Action.Level is "hard-affinity" or "job-enforced" or "job-locked"
                 || r.Action.GetPreferredMask() != 0))
            { anyOngoingRule = true; break; }
        }
        bool anyManual = ManualAffinityRegistry.ManualWins &&
                         (!ManualAffinityRegistry.IsEmpty || !PersistentAffinityStore.IsEmpty);
        if (!anyOngoingRule && !anyManual)
        {
            Log.Information("Watchdog idle: enabledRules={Enabled} ongoing={Ongoing} manual={Manual} ManualWins={Wins}",
                enabledRuleCount, anyOngoingRule, anyManual, ManualAffinityRegistry.ManualWins);
            return 0;
        }

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

                string procName = process.ProcessName + ".exe";
                if (procName.StartsWith("waketest", StringComparison.OrdinalIgnoreCase))
                {
                    long now = Environment.TickCount64;
                    if (now - LastWaketestLog > 1000)
                    {
                        LastWaketestLog = now;
                        Log.Information("WATCHDOG-LOOP: seen {Name} (pid {Pid}), ManualWins={Mw}", procName, pid, ManualAffinityRegistry.ManualWins);
                    }
                }

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

                string name = procName;

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

                // 诊断:新进程出现但未匹配任何规则时记录一次
                var diagRule = _ruleEngine.Match(name, path);
                if (diagRule == null || !RequiresOngoingEnforcement(diagRule))
                {
                    if (name.StartsWith("waketest", StringComparison.OrdinalIgnoreCase))
                        Log.Information("WATCHDOG-SKIP: {Name} (pid {Pid}) matched={Matched}", name, pid, diagRule?.Name ?? "NULL");
                }

                RuleEntry? rule = _ruleEngine.Match(name, path);
                if (rule?.Action == null || !RequiresOngoingEnforcement(rule))
                    continue;
                LogMatchOnce(pid, name, rule);

                // “优先调度核心”每个周期重设一次:覆盖进程后创建的新线程,
                // 也纠正游戏自己改回的理想处理器 —— 这是它真正“生效”的关键。
                ulong prefer = rule.Action.GetPreferredMask();
                if (prefer != 0)
                    ProcOps.ProcessControlService.ApplyPreferredCores(pid, prefer, rule.Action.GetPreferMode());

                ulong expected = CpuTopology.BuildMask(topology, rule.Action.Mode, rule.Action.GetCustomMask());
                string wpm = rule.Action.GetPreferMode();
                if (wpm is "static" or "d2")
                {
                    ulong poolMask = rule.Action.GetSchedulingPoolMask();
                    if (poolMask != 0)
                        expected &= poolMask;
                }
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
            catch (Exception ex)
            {
                // Process may exit or deny access while scanning.
                if (ex is not InvalidOperationException)
                    Log.Warning(ex, "WATCHDOG per-process error (pid {Pid})", process.Id);
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

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            int changed = EnforceOnce();
            if (sw.ElapsedMilliseconds > 500)
                Log.Information("Watchdog tick took {Ms}ms (changed {N})", sw.ElapsedMilliseconds, changed);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Affinity watchdog tick failed after {Ms}ms", sw.ElapsedMilliseconds);
        }
        finally
        {
            Volatile.Write(ref _isTicking, 0);
        }
    }

    internal static string LastTickLog = "";

    // 节流诊断:每个进程每秒最多记一条"已匹配"日志,用于定位 watchdog 应用延迟。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> MatchLogThrottle = new();
    private static long LastWaketestLog;

    private static void LogMatchOnce(int pid, string name, RuleEntry rule)
    {
        long now = Environment.TickCount64;
        if (MatchLogThrottle.TryGetValue(pid, out long last) && now - last < 1000)
            return;
        MatchLogThrottle[pid] = now;
        Log.Information("Watchdog matched '{Rule}' -> {Process} (pid {Pid})", rule.Name, name, pid);
    }

    private static bool RequiresOngoingEnforcement(RuleEntry rule)
    {
        return rule.Action.Level is "hard-affinity" or "job-enforced" or "job-locked"
            || rule.Action.GetPreferredMask() != 0;
    }

    public void Dispose() => Stop();
}// INTENTIONAL-COMPILE-TEST
