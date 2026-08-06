using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CpuAffinityManager.Cpu;
using CpuAffinityManager.Native;
using Serilog;

namespace CpuAffinityManager.ProcOps;

/// <summary>
/// Per-process control operations for the "professional" feature set:
/// suspend/resume/terminate, priority class, IO priority, memory priority,
/// efficiency mode (EcoQoS), dynamic-priority boost, empty working set, and
/// physical working-set limits. All calls are best-effort and never throw.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProcessControlService
{
    // ── priority ──
    public static bool SetPriority(int pid, ProcessPriorityClass priority)
        => Managed(pid, p => p.PriorityClass = priority);

    /// <summary>Enable/disable the OS dynamic-priority boost for the process.</summary>
    public static bool SetPriorityBoost(int pid, bool enabled)
        => Managed(pid, p => p.PriorityBoostEnabled = enabled);

    // ── lifecycle ──
    public static bool Terminate(int pid)
        => Managed(pid, p => p.Kill());

    /// <summary>Terminates the process together with its entire child tree.</summary>
    public static bool TerminateTree(int pid)
        => Managed(pid, p => p.Kill(entireProcessTree: true));

    // ── thread-level scheduling ──
    /// <summary>
    /// Distributes the process's threads across the cores in <paramref name="mask"/>:
    /// each thread is confined to the mask and its ideal processor is assigned
    /// round-robin, spreading load instead of piling every thread on one core.
    /// </summary>
    public static bool DistributeThreads(int pid, ulong mask)
    {
        var cores = new List<int>();
        for (int i = 0; i < 64; i++) if ((mask & (1UL << i)) != 0) cores.Add(i);
        if (cores.Count == 0) return false;

        return Managed(pid, p =>
        {
            IntPtr aff = (IntPtr)(long)mask;
            int idx = 0;
            foreach (ProcessThread t in p.Threads)
            {
                try
                {
                    t.ProcessorAffinity = aff;
                    t.IdealProcessor = cores[idx % cores.Count];
                }
                catch { /* system threads may deny access */ }
                idx++;
            }
        });
    }

    public static bool Suspend(int pid) => WithHandle(pid, PROCESS_SUSPEND_RESUME, h => NtSuspendProcess(h) == 0);
    public static bool Resume(int pid) => WithHandle(pid, PROCESS_SUSPEND_RESUME, h => NtResumeProcess(h) == 0);

    // ── IO priority (0=verylow,1=low,2=normal,3=high) ──
    public static bool SetIoPriority(int pid, int level)
        => WithHandle(pid, PROCESS_SET_INFORMATION, h =>
        {
            uint io = (uint)Math.Clamp(level, 0, 3);
            return NtSetInformationProcess(h, ProcessIoPriority, ref io, sizeof(uint)) == 0;
        });

    // ── memory priority (1=very low … 5=normal) ──
    public static bool SetMemoryPriority(int pid, int priority)
        => WithHandle(pid, PROCESS_SET_INFORMATION, h =>
        {
            var info = new MEMORY_PRIORITY_INFORMATION { MemoryPriority = (uint)Math.Clamp(priority, 1, 5) };
            return SetProcessInformation(h, PROC_INFO_MemoryPriority, ref info, (uint)Marshal.SizeOf<MEMORY_PRIORITY_INFORMATION>());
        });

    /// <summary>Efficiency mode (EcoQoS): throttle execution speed + idle priority.</summary>
    public static bool SetEfficiencyMode(int pid, bool on)
        => WithHandle(pid, PROCESS_SET_INFORMATION, h =>
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = 1,
                ControlMask = EXECUTION_SPEED,
                StateMask = on ? EXECUTION_SPEED : 0
            };
            bool ok = SetProcessInformation(h, PROC_INFO_PowerThrottling, ref state, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
            // EcoQoS is most effective paired with the Idle priority class.
            Managed(pid, p => p.PriorityClass = on ? ProcessPriorityClass.Idle : ProcessPriorityClass.Normal);
            return ok;
        });

    // ── working set (physical memory) ──
    /// <summary>Trims the process working set (releases physical memory back to the OS).</summary>
    public static bool EmptyWorkingSet(int pid)
        => WithHandle(pid, PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA, h => K32EmptyWorkingSet(h));

    /// <summary>
    /// Caps the process's committed (virtual) memory via a Job Object process-memory
    /// limit (megabytes). On Win8+ this nests with any existing job. Note: a process
    /// cannot be removed from a job, so this holds until the process exits.
    /// </summary>
    public static bool LimitVirtualMemory(int pid, int megabytes)
    {
        if (megabytes <= 0) return false;
        IntPtr h = OpenProcess(PROCESS_SET_QUOTA | PROCESS_TERMINATE | PROCESS_QUERY_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return false;
        IntPtr hJob = IntPtr.Zero;
        try
        {
            hJob = Kernel32Imports.CreateJobObject(IntPtr.Zero, $"CamMemLimit_{pid}");
            if (hJob == IntPtr.Zero) return false;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JobLimitFlags.JOB_OBJECT_LIMIT_PROCESS_MEMORY
                },
                ProcessMemoryLimit = (UIntPtr)((ulong)megabytes * 1024 * 1024)
            };

            int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!Kernel32Imports.SetInformationJobObject(hJob, JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, ptr, (uint)size))
                    return false;
            }
            finally { Marshal.FreeHGlobal(ptr); }

            return Kernel32Imports.AssignProcessToJobObject(hJob, h);
        }
        catch (Exception ex) { Log.Debug(ex, "LimitVirtualMemory failed pid {Pid}", pid); return false; }
        finally
        {
            if (hJob != IntPtr.Zero) Kernel32Imports.CloseHandle(hJob);
            CloseHandle(h);
        }
    }

    /// <summary>Caps the process physical working set (min/max in megabytes; 0 = unset).</summary>
    public static bool LimitWorkingSet(int pid, int minMb, int maxMb)
        => WithHandle(pid, PROCESS_SET_QUOTA | PROCESS_QUERY_INFORMATION, h =>
        {
            UIntPtr min = (UIntPtr)((ulong)Math.Max(0, minMb) * 1024 * 1024);
            UIntPtr max = (UIntPtr)((ulong)Math.Max(minMb, maxMb) * 1024 * 1024);
            uint flags = maxMb > 0 ? QUOTA_LIMITS_HARDWS_MAX_ENABLE : QUOTA_LIMITS_HARDWS_MAX_DISABLE;
            return SetProcessWorkingSetSizeEx(h, min, max, flags);
        });

    /// <summary>
    /// Makes the process run on a single logical core: sets the process affinity to just
    /// <paramref name="core"/> AND every thread's ideal processor to it. This is the
    /// effective "让程序跑在某个核心" (soft ideal + hard affinity to that core).
    /// </summary>
    public static bool PreferCore(int pid, int core)
    {
        if (core < 0 || core > 63) return false;
        UIntPtr mask = (UIntPtr)(1UL << core);

        bool ok = WithHandle(pid, PROCESS_SET_INFORMATION | PROCESS_QUERY_INFORMATION, h =>
        {
            int st = NtdllImports.NtSetInformationProcess(h, PROCESS_INFORMATION_CLASS.ProcessAffinityMask, ref mask, (uint)UIntPtr.Size);
            if (st == 0) return true;
            return Kernel32Imports.SetProcessAffinityMask(h, mask);
        });

        Managed(pid, p =>
        {
            foreach (ProcessThread t in p.Threads)
            {
                try { t.IdealProcessor = core; } catch { }
            }
        });
        return ok;
    }

    // ── GPU scheduling priority ──
    /// <summary>
    /// Sets the process's GPU scheduling priority class via the WDDM kernel
    /// (D3DKMTSetProcessSchedulingPriorityClass). This is the GPU-side analogue of the
    /// CPU priority class and is independent of it.
    /// class: 0=空闲 1=低于常规 2=常规 3=高于常规 4=高 5=实时.
    /// </summary>
    public static bool SetGpuPriority(int pid, int gpuClass)
        => WithHandle(pid, PROCESS_SET_INFORMATION | PROCESS_QUERY_INFORMATION, h =>
            D3DKMTSetProcessSchedulingPriorityClass(h, (uint)Math.Clamp(gpuClass, 0, 5)) == 0);

    // ── 核心分配算法(线程级自适应核心分配) ──
    /// <summary>
    /// Thread-level adaptive core-allocation "algorithm". Emulates the multi-generation
    /// schemes seen in similar tuners: it ranks the process's threads by accumulated CPU
    /// time (the busiest ≈ the game's main/render "key" threads) and then places them on
    /// preferred cores. Generations differ along four axes — scope (main-thread-only vs
    /// all-threads), core pool (P-cores / physical-only P-cores / all cores), binding
    /// (soft ideal-processor hint vs hard single-core affinity), and whether background
    /// threads are pushed onto E-cores — giving a conservative→aggressive gradient.
    /// Algorithm ids 1..12 match the UI list. Returns false if id is unknown.
    /// </summary>
    public static bool ApplyCoreAlgorithm(int pid, int algorithm, CpuTopology topo)
    {
        AlgoConfig? maybe = ConfigFor(algorithm);
        if (maybe is not AlgoConfig cfg) return false;

        int total = topo.TotalLogicalProcessors <= 0 ? Environment.ProcessorCount : topo.TotalLogicalProcessors;
        ulong all   = CpuTopology.ClampToLogicalProcessors(~0UL, total);
        ulong pMask = CpuTopology.ClampToLogicalProcessors(topo.PcoreMask != 0 ? topo.PcoreMask : all, total);
        ulong phys  = CpuTopology.ClampToLogicalProcessors(topo.Smt0Mask != 0 ? (pMask & topo.Smt0Mask) : pMask, total);
        ulong eMask = CpuTopology.ClampToLogicalProcessors(topo.EcoreMask, total);

        var primaryPool = CoresOf(cfg.AllCores ? all : (cfg.PhysicalOnly ? phys : pMask));
        if (primaryPool.Count == 0) primaryPool = CoresOf(all);
        var bgPool = cfg.BackgroundToEcore && eMask != 0 ? CoresOf(eMask) : primaryPool;
        if (bgPool.Count == 0) bgPool = primaryPool;

        ulong poolMask = 0; foreach (var c in primaryPool) poolMask |= 1UL << c;
        ulong bgMask   = 0; foreach (var c in bgPool)      bgMask   |= 1UL << c;

        return Managed(pid, p =>
        {
            var threads = p.Threads.Cast<ProcessThread>()
                .OrderByDescending(t => { try { return t.TotalProcessorTime; } catch { return TimeSpan.Zero; } })
                .ToList();
            if (threads.Count == 0) return;

            int keyN = cfg.MainOnly ? 1 : (cfg.DedicateKeyThreads ? Math.Min(cfg.KeyThreadCount, primaryPool.Count) : 0);

            for (int i = 0; i < threads.Count; i++)
            {
                var t = threads[i];
                try
                {
                    if (i < keyN)
                    {
                        // Key thread → its own preferred core (dedicated when hard-bound).
                        int core = primaryPool[i % primaryPool.Count];
                        t.IdealProcessor = core;
                        if (cfg.Hard) t.ProcessorAffinity = (IntPtr)(1L << core);
                        if (cfg.BoostKeyThreads) { try { t.PriorityLevel = ThreadPriorityLevel.AboveNormal; } catch { } }
                    }
                    else if (cfg.MainOnly)
                    {
                        break; // 第一代:仅调整主线程,其余保持系统默认
                    }
                    else
                    {
                        // Remaining threads spread across the (background) pool round-robin.
                        var pool = cfg.BackgroundToEcore && bgPool.Count > 0 ? bgPool : primaryPool;
                        ulong pm = cfg.BackgroundToEcore && bgPool.Count > 0 ? bgMask : poolMask;
                        int core = pool[i % pool.Count];
                        t.IdealProcessor = core;
                        if (cfg.Hard) t.ProcessorAffinity = (IntPtr)(long)pm;
                    }
                }
                catch { /* system/denied threads */ }
            }
        });
    }

    private static List<int> CoresOf(ulong mask)
    {
        var l = new List<int>();
        for (int i = 0; i < 64; i++) if ((mask & (1UL << i)) != 0) l.Add(i);
        return l;
    }

    private readonly struct AlgoConfig
    {
        public bool MainOnly { get; init; }
        public bool AllCores { get; init; }
        public bool PhysicalOnly { get; init; }
        public bool BackgroundToEcore { get; init; }
        public bool Hard { get; init; }
        public bool DedicateKeyThreads { get; init; }
        public bool BoostKeyThreads { get; init; }
        public int KeyThreadCount { get; init; }
    }

    private static AlgoConfig? ConfigFor(int a) => a switch
    {
        1  => new AlgoConfig { MainOnly = true },                                                                    // 主线程·P核·软·保守
        2  => new AlgoConfig { MainOnly = true, PhysicalOnly = true, Hard = true },                                  // 主线程·物理核独占·激进
        3  => new AlgoConfig { },                                                                                    // 全线程·P核·软
        4  => new AlgoConfig { AllCores = true },                                                                    // 全线程·全核·软
        5  => new AlgoConfig { DedicateKeyThreads = true, KeyThreadCount = 2 },                                      // 关键线程优选·保守
        6  => new AlgoConfig { DedicateKeyThreads = true, KeyThreadCount = 2, PhysicalOnly = true },                 // 关键线程·物理核·保守
        7  => new AlgoConfig { DedicateKeyThreads = true, KeyThreadCount = 2, Hard = true },                         // 关键线程独占·激进
        8  => new AlgoConfig { DedicateKeyThreads = true, KeyThreadCount = 2, Hard = true, PhysicalOnly = true, BackgroundToEcore = true }, // 独占+后台入小核·激进
        9  => new AlgoConfig { DedicateKeyThreads = true, KeyThreadCount = 3, BackgroundToEcore = true },            // 动态+后台小核·保守
        10 => new AlgoConfig { DedicateKeyThreads = true, KeyThreadCount = 3, BackgroundToEcore = true, BoostKeyThreads = true }, // 动态+提权·保守
        11 => new AlgoConfig { DedicateKeyThreads = true, KeyThreadCount = 4, Hard = true, PhysicalOnly = true, BackgroundToEcore = true }, // 独占物理核+后台小核·激进
        12 => new AlgoConfig { DedicateKeyThreads = true, KeyThreadCount = 4, Hard = true, PhysicalOnly = true, BackgroundToEcore = true, BoostKeyThreads = true }, // 极限·激进
        _  => null
    };

    /// <summary>
    /// Confines the process to an arbitrary set of logical cores (bitmask). Sets the
    /// process affinity via NtSetInformationProcess (falling back to SetProcessAffinityMask)
    /// AND re-applies the same mask to every thread, so any earlier per-thread pinning from
    /// a core-allocation algorithm is cleared and the process-level mask actually governs.
    /// This is the reliable, multi-core version of "优先跑指定核心".
    /// </summary>
    public static bool SetAffinityMask(int pid, ulong mask)
    {
        if (mask == 0) return false;
        UIntPtr m = (UIntPtr)mask;
        bool ok = WithHandle(pid, PROCESS_SET_INFORMATION | PROCESS_QUERY_INFORMATION, h =>
        {
            int st = NtdllImports.NtSetInformationProcess(h, PROCESS_INFORMATION_CLASS.ProcessAffinityMask, ref m, (uint)UIntPtr.Size);
            if (st == 0) return true;
            return Kernel32Imports.SetProcessAffinityMask(h, m);
        });
        Managed(pid, p =>
        {
            foreach (ProcessThread t in p.Threads)
                try { t.ProcessorAffinity = (IntPtr)(long)mask; } catch { }
        });
        return ok;
    }

    /// <summary>
    /// HARD-LOCKS the process to a core set via a Job Object affinity limit
    /// (JOB_OBJECT_LIMIT_AFFINITY). Unlike SetAffinityMask, the process itself cannot
    /// change its affinity back afterwards. Like the virtual-memory limit, a process
    /// cannot leave a job, so the lock holds until the process exits (or reboot).
    /// </summary>
    public static bool LockAffinityMask(int pid, ulong mask)
    {
        if (mask == 0) return false;
        SetAffinityMask(pid, mask); // set the mask first (also clears any thread pinning)

        IntPtr h = OpenProcess(PROCESS_SET_QUOTA | PROCESS_TERMINATE | PROCESS_QUERY_INFORMATION | PROCESS_SET_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return false;
        IntPtr hJob = IntPtr.Zero;
        try
        {
            hJob = Kernel32Imports.CreateJobObject(IntPtr.Zero, $"CamAffinityLock_{pid}");
            if (hJob == IntPtr.Zero) return false;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JobLimitFlags.JOB_OBJECT_LIMIT_AFFINITY,
                    Affinity = (UIntPtr)mask
                }
            };
            int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!Kernel32Imports.SetInformationJobObject(hJob, JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, ptr, (uint)size))
                    return false;
            }
            finally { Marshal.FreeHGlobal(ptr); }

            return Kernel32Imports.AssignProcessToJobObject(hJob, h);
        }
        catch (Exception ex) { Log.Debug(ex, "LockAffinityMask failed pid {Pid}", pid); return false; }
        finally
        {
            if (hJob != IntPtr.Zero) Kernel32Imports.CloseHandle(hJob);
            CloseHandle(h);
        }
    }

    /// <summary>
    /// Undoes a core-allocation algorithm: restores the process (and every thread) to the
    /// full core set and resets thread priority levels to Normal.
    /// </summary>
    public static bool ResetThreadScheduling(int pid, ulong allMask)
    {
        bool ok = SetAffinityMask(pid, allMask);
        Managed(pid, p =>
        {
            foreach (ProcessThread t in p.Threads)
                try { t.PriorityLevel = ThreadPriorityLevel.Normal; } catch { }
        });
        return ok;
    }

    /// <summary>
    /// Soft "优先核心": sets every thread's ideal processor to <paramref name="core"/> WITHOUT
    /// changing affinity, so the process can still run on all its cores but Windows schedules
    /// it on the preferred core first. Used by the rule action "全核可用·优先某核心".
    /// </summary>
    public static bool SetIdealCore(int pid, int core)
    {
        if (core < 0 || core > 63) return false;
        return Managed(pid, p =>
        {
            foreach (ProcessThread t in p.Threads)
                try { t.IdealProcessor = core; } catch { }
        });
    }

    /// <summary>
    /// Multi-core priority scheduling with binding mode:
    ///   "dynamic" / null → 游戏模式:最热线程(主/渲染线程)硬钉在优先核心,
    ///                        其余线程全核可用(工作线程可满载);理想处理器同时设置
    ///   "static"        → set process+thread affinity to priority mask (hard bind)
    ///   "d2"            → CPU Sets to priority mask + IdealProcessor (moderate)
    ///   "d3"            → IdealProcessor to priority mask + EcoQoS (省电)
    /// Re-applied periodically by the watchdog (250ms), so新线程会很快被识别为热线程。
    /// </summary>
    public static bool ApplyPreferredCores(int pid, ulong preferredMask, string? preferMode = null)
    {
        var cores = CoresOf(preferredMask);
        if (cores.Count == 0) return false;

        bool ok = true;
        string mode = !string.IsNullOrWhiteSpace(preferMode) ? preferMode : "dynamic";

        switch (mode)
        {
            case "static":
                ok = SetAffinityMask(pid, preferredMask);
                break;
            case "d2":
                ok = SetDefaultCpuSets(pid, preferredMask);
                break;
            case "d3":
                SetEfficiencyMode(pid, true);
                break;
            default: // dynamic — 游戏主线程优先核心硬钉
                ok = PinHottestThreads(pid, cores);
                break;
        }

        return ok;
    }

    /// <summary>
    /// 优先核心调度:恰好 1 个线程正在执行(单核负载)时把它钉到优先核心;
    /// ≥2 个线程同时执行(多核负载)时完全放开让全核满载;无线程执行时保持现状。
    ///
    /// 用线程实时状态(Running/Wait)而非 CPU 时间统计判定 —— 某些环境
    /// (虚拟化/沙箱)下 GetProcessTimes/性能计数器的 CPU 时间统计会失真,
    /// 基于 CPU 增量的判定会漏钉或误判,而线程状态是可靠的实时信号。
    /// </summary>
    private static bool PinHottestThreads(int pid, List<int> preferredCores)
    {
        return Managed(pid, p =>
        {
            ulong allCores = (1UL << Environment.ProcessorCount) - 1;
            if (allCores == 0) allCores = ulong.MaxValue;

            var threads = p.Threads.Cast<ProcessThread>().ToList();
            if (threads.Count == 0) return;

            var running = threads.Where(t =>
            {
                try { return t.ThreadState == System.Diagnostics.ThreadState.Running; }
                catch { return false; }
            }).ToList();

            // 无线程正在执行(空闲/全部在等待):保持现状不动,避免抖动。
            if (running.Count == 0)
            {
                CleanStalePins(pid, threads);
                return;
            }

            // 多核负载判定:≥4 个线程同时在执行才视为多核满载并解除钉扎。
            // 用 4 而不是 2 —— 单核测试进程(如 CPU-Z)里 UI/服务线程也会
            // Running(共 2-3 个),若阈值过低会把单核测试误判为多核而不钉。
            const int MultiCoreRunningThreshold = 4;
            if (running.Count >= MultiCoreRunningThreshold)
            {
                UnpinAll(pid, threads, allCores);
                UnpinCooldown[pid] = Environment.TickCount64 + 2000; // 解除后 2 秒冷却,防震荡
                CleanStalePins(pid, threads);
                return;
            }

            // 冷却期内(刚解除多核钉扎)不立即重钉。
            if (UnpinCooldown.TryGetValue(pid, out long until) && Environment.TickCount64 < until)
            {
                CleanStalePins(pid, threads);
                return;
            }

            // 单核负载(1-3 个线程在执行):在 running 线程中选累计 CPU 时间
            // 最高的(测试线程 >> UI 线程,相对排序即使在 CPU 统计失真的环境
            // 下仍有区分度)钉到优先核心。
            var hottest = running.OrderByDescending(t => SafeCpuTime(t)).First();
            var chosen = new List<ProcessThread> { hottest };

            int idx = 0;
            foreach (var t in chosen)
            {
                int core = preferredCores[idx % preferredCores.Count];
                idx++;
                try { t.IdealProcessor = core; } catch { }
                try
                {
                    // 无条件设置线程亲和性(幂等):即使 PinnedThreads 里残留了复用的
                    // PID/线程ID 旧记录,也能保证钉扎真实生效,而不是只留下软提示。
                    t.ProcessorAffinity = new IntPtr(1L << core);
                    if (GetPinnedCore(pid, t.Id) != core)
                    {
                        SetPinnedCore(pid, t.Id, core);
                        Log.Information("PIN tid {Tid} -> core {Core} (pid {Pid})", t.Id, core, pid);
                    }
                }
                catch (Exception ex) { Log.Warning(ex, "PIN failed tid {Tid} pid {Pid}", t.Id, pid); }
            }

            // 非钉扎线程:若之前被我们钉过(有真实 core 记录),恢复全核可用。
            foreach (var t in threads)
            {
                if (chosen.Contains(t)) continue;
                try
                {
                    int? pinned = GetPinnedCore(pid, t.Id);
                    if (pinned.HasValue)
                    {
                        t.ProcessorAffinity = new IntPtr((long)allCores);
                        ClearPinnedCore(pid, t.Id);
                        Log.Information("UNPIN tid {Tid} (pid {Pid})", t.Id, pid);
                    }
                }
                catch (Exception ex) { Log.Warning(ex, "UNPIN failed tid {Tid} pid {Pid}", t.Id, pid); }
            }

            CleanStalePins(pid, threads);
        });
    }

    private static void UnpinAll(int pid, List<ProcessThread> threads, ulong allCores)
    {
        foreach (var t in threads)
        {
            if (!GetPinnedCore(pid, t.Id).HasValue) continue;
            try
            {
                t.ProcessorAffinity = new IntPtr((long)allCores);
                ClearPinnedCore(pid, t.Id);
                Log.Information("UNPIN tid {Tid} (pid {Pid})", t.Id, pid);
            }
            catch (Exception ex) { Log.Warning(ex, "UNPIN failed tid {Tid} pid {Pid}", t.Id, pid); }
        }
    }

    // 钉扎记录 (pid, tid) → core(仅真正钉过的线程),用于解除钉扎。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, int> PinnedThreads = new();

    // 进程级 CPU 时间快照 (pid) → 上次读取值,用于计算进程总增量。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, double> LastProcCpuSnapshot = new();

    // 单核负载连续确认计数 (pid) → 连续观察到的单核 tick 数。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> SingleCoreStreak = new();

    // 多核解除后的冷却截止时间 (pid) → TickCount64,冷却期内不重钉,防震荡。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> UnpinCooldown = new();

    private static long PinKey(int pid, int tid) => ((long)pid << 32) | (uint)tid;

    private static int? GetPinnedCore(int pid, int tid)
        => PinnedThreads.TryGetValue(PinKey(pid, tid), out int core) ? core : null;

    private static double GetLastProcCpu(int pid)
        => LastProcCpuSnapshot.TryGetValue(pid, out double cpu) ? cpu : 0;

    private static void SetLastProcCpu(int pid, double cpu)
        => LastProcCpuSnapshot[pid] = cpu;

    private static void SetPinnedCore(int pid, int tid, int core) => PinnedThreads[PinKey(pid, tid)] = core;

    private static void ClearPinnedCore(int pid, int tid) => PinnedThreads.TryRemove(PinKey(pid, tid), out _);

    private static void CleanStalePins(int pid, List<ProcessThread> threads)
    {
        var alive = new HashSet<long>(threads.Select(t => PinKey(pid, t.Id)));
        foreach (var key in PinnedThreads.Keys)
        {
            if ((key >> 32) == pid && !alive.Contains(key))
                PinnedThreads.TryRemove(key, out _);
        }
        if (PinnedThreads.Count > 8192)
            PinnedThreads.Clear();
        if (LastProcCpuSnapshot.Count > 4096)
            LastProcCpuSnapshot.Clear(); // 防御性上限
    }

    private static double SafeCpuTime(ProcessThread t)
    {
        try { return t.TotalProcessorTime.TotalSeconds; }
        catch { return 0; }
    }

    /// <summary>
    /// Sets (mask != 0) or clears (mask == 0) a process's default CPU Sets, translating
    /// the logical-core bitmask into real CPU-set IDs first.
    /// </summary>
    public static bool SetDefaultCpuSets(int pid, ulong mask)
        => WithHandle(pid, PROCESS_SET_LIMITED_INFORMATION | PROCESS_QUERY_INFORMATION, h =>
        {
            if (mask == 0)
                return Kernel32Imports.SetProcessDefaultCpuSets(h, null, 0);
            uint[]? ids = CpuSetIdsFor(mask);
            if (ids == null || ids.Length == 0) return false;
            return Kernel32Imports.SetProcessDefaultCpuSets(h, ids, (uint)ids.Length);
        });

    // Cached logical-processor-index → CPU-set-ID table (stable for the session).
    private static uint[]? _cpuSetIdByCore;
    private static bool[]? _cpuSetPopulated;

    /// <summary>Maps a core bitmask to the matching CPU-set IDs via GetSystemCpuSetInformation.</summary>
    private static uint[]? CpuSetIdsFor(ulong mask)
    {
        try
        {
            if (_cpuSetIdByCore == null)
            {
                Kernel32Imports.GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint len, IntPtr.Zero, 0);
                if (len == 0) return null;
                IntPtr buf = Marshal.AllocHGlobal((int)len);
                try
                {
                    if (!Kernel32Imports.GetSystemCpuSetInformation(buf, len, out len, IntPtr.Zero, 0))
                        return null;
                    var table = new uint[64];
                    var populated = new bool[64];
                    IntPtr cur = buf;
                    long end = buf.ToInt64() + len;
                    while (cur.ToInt64() < end)
                    {
                        var info = Marshal.PtrToStructure<SYSTEM_CPU_SET_INFORMATION>(cur);
                        if (info.Size == 0) break;
                        if (info.Type == CPU_SET_INFORMATION_TYPE.CpuSetInformation &&
                            info.CpuSet.LogicalProcessorIndex < 64)
                        {
                            table[info.CpuSet.LogicalProcessorIndex] = info.CpuSet.Id;
                            populated[info.CpuSet.LogicalProcessorIndex] = true;
                        }
                        cur = (IntPtr)(cur.ToInt64() + info.Size);
                    }
                    _cpuSetIdByCore = table;
                    _cpuSetPopulated = populated;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }

            var ids = new List<uint>();
            for (int i = 0; i < 64; i++)
                if ((mask & (1UL << i)) != 0 && _cpuSetPopulated[i])
                    ids.Add(_cpuSetIdByCore[i]);
            return ids.ToArray();
        }
        catch (Exception ex) { Log.Debug(ex, "CpuSetIdsFor failed"); return null; }
    }

    // ── window control ──
    /// <summary>Restores and brings the process's main window to the foreground.</summary>
    public static bool BringToFront(int pid) => Window(pid, h => { ShowWindow(h, SW_RESTORE); return SetForegroundWindow(h); });
    public static bool MinimizeWindow(int pid) => Window(pid, h => ShowWindow(h, SW_MINIMIZE));
    public static bool RestoreWindow(int pid) => Window(pid, h => ShowWindow(h, SW_RESTORE));

    private static bool Window(int pid, Func<IntPtr, bool> act)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            IntPtr h = p.MainWindowHandle;
            if (h == IntPtr.Zero) return false;
            return act(h);
        }
        catch (Exception ex) { Log.Debug(ex, "window op failed pid {Pid}", pid); return false; }
    }

    // ── helpers ──
    private static bool Managed(int pid, Action<Process> act)
    {
        try { using var p = Process.GetProcessById(pid); act(p); return true; }
        catch (Exception ex) { Log.Debug(ex, "process op failed pid {Pid}", pid); return false; }
    }

    private static bool WithHandle(int pid, uint access, Func<IntPtr, bool> act)
    {
        IntPtr h = OpenProcess(access, false, (uint)pid);
        if (h == IntPtr.Zero) return false;
        try { return act(h); }
        catch (Exception ex) { Log.Debug(ex, "native process op failed pid {Pid}", pid); return false; }
        finally { CloseHandle(h); }
    }

    // ── access rights ──
    private const uint PROCESS_TERMINATE = 0x0001;
    private const uint PROCESS_SET_QUOTA = 0x0100;
    private const uint PROCESS_SET_INFORMATION = 0x0200;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_SUSPEND_RESUME = 0x0800;
    private const uint PROCESS_SET_LIMITED_INFORMATION = 0x2000;

    // NtSetInformationProcess class for IO priority
    private const int ProcessIoPriority = 33;
    // Documented SetProcessInformation classes
    private const int PROC_INFO_MemoryPriority = 0;
    private const int PROC_INFO_PowerThrottling = 4;
    private const uint EXECUTION_SPEED = 0x1;
    private const uint QUOTA_LIMITS_HARDWS_MAX_ENABLE = 0x00000004;
    private const uint QUOTA_LIMITS_HARDWS_MAX_DISABLE = 0x00000008;

    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_PRIORITY_INFORMATION { public uint MemoryPriority; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE { public uint Version; public uint ControlMask; public uint StateMask; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool K32EmptyWorkingSet(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSizeEx(IntPtr h, UIntPtr min, UIntPtr max, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr h, int infoClass, ref MEMORY_PRIORITY_INFORMATION info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr h, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, uint size);

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr h);
    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr h);
    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationProcess(IntPtr h, int infoClass, ref uint info, uint len);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    // WDDM GPU scheduling priority class. Takes a real process HANDLE (not a D3DKMT handle).
    [DllImport("gdi32.dll")]
    private static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr hProcess, uint priorityClass);
}