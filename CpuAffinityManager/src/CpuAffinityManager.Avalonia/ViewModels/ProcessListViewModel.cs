using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CpuAffinityManager.ProcOps;
using CpuAffinityManager.Cpu;
using CpuAffinityManager.Engine;
using CpuAffinityManager.Enforcement;
using CpuAffinityManager.Monitoring;
using Serilog;

namespace CpuAffinityManager.Avalonia.ViewModels;

public partial class ProcessListViewModel : ViewModelBase
{
    private readonly IRuleEngine _ruleEngine;
    private readonly IEnforcementService _enforcementService;
    private readonly ICpuTopologyService _topoService;
    private CancellationTokenSource? _refreshCts;

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _filterMode = "全部进程";
    [ObservableProperty] private string _sortMode = "名称";
    [ObservableProperty] private bool _autoRefresh;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _loadError = "";

    private List<ProcessItem> _allItems = new();
    private readonly Dictionary<int, (TimeSpan cpu, long tick)> _prevCpu = new();
    private DispatcherTimer? _autoTimer;
    public ObservableCollection<ProcessItem> Processes { get; } = new();
    public static string[] FilterModes { get; } = ["全部进程", "已匹配规则", "Job 强制"];
    public static string[] SortModes { get; } = ["名称", "CPU", "内存", "PID"];

    /// <summary>“核心分配算法”子菜单项(线程级自适应,12 种代际算法)。</summary>
    public ObservableCollection<CoreChoice> AlgoChoices { get; } = new();

    // 12 种核心分配算法(第一/四/五/六代;5、6 代分保守与激进)。id 传给核心引擎。
    private static readonly (int Id, string Label)[] CoreAlgoList =
    {
        (1,  "第一代算法1 (主线程·软)"),
        (2,  "第一代算法2 (主线程·独占物理核)"),
        (3,  "第四代算法1 (全线程·大核)"),
        (4,  "第四代算法2 (全线程·全核)"),
        (5,  "第五代算法1 (关键线程优选·保守)"),
        (6,  "第五代算法2 (关键线程·物理核·保守)"),
        (7,  "第五代算法3 (关键线程独占·激进)"),
        (8,  "第五代算法4 (独占+后台入小核·激进)"),
        (9,  "第六代算法1 (动态+后台小核·保守)"),
        (10, "第六代算法2 (动态+关键线程提权·保守)"),
        (11, "第六代算法3 (独占物理核+后台小核·激进)"),
        (12, "第六代算法4 (极限·激进)"),
    };

    partial void OnSortModeChanged(string value) => ApplyFilter();

    partial void OnFilterModeChanged(string value) => ApplyFilter();

    partial void OnAutoRefreshChanged(bool value)
    {
        if (value)
        {
            _autoTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _autoTimer.Tick -= AutoTick;
            _autoTimer.Tick += AutoTick;
            _autoTimer.Start();
        }
        else { _autoTimer?.Stop(); }
    }

    private void AutoTick(object? sender, EventArgs e)
    {
        // 内存优化:进程页不在前台时跳过自动刷新——不然每 2 秒枚举全部进程
        // 会持续产生几百个临时对象与句柄,推高常驻内存。
        if (Parent != null && !ReferenceEquals(Parent.CurrentPage, this)) return;
        Refresh();
    }

    public MainWindowViewModel? Parent { get; set; }

    public ProcessListViewModel(IRuleEngine ruleEngine, IEnforcementService enforcementService, ICpuTopologyService topoService)
    {
        _ruleEngine = ruleEngine;
        _enforcementService = enforcementService;
        _topoService = topoService;
        foreach (var (id, label) in CoreAlgoList)
        {
            int a = id; // capture
            AlgoChoices.Add(new CoreChoice(label, new RelayCommand(() => ApplyCoreAlgorithmOn(a))));
        }
    }

    /// <summary>右键选中某个算法时,对当前行进程应用线程级核心分配算法。</summary>
    private void ApplyCoreAlgorithmOn(int algo)
    {
        var i = ContextItem;
        if (i == null) return;
        Do(i, () => ProcessControlService.ApplyCoreAlgorithm(i.Pid, algo, _topoService.Detect()),
            $"已对 {i.Name} 应用核心分配算法 {algo}(线程级)", "核心分配算法应用失败");
    }

    /// <summary>本机全部逻辑核的掩码(用于“还原全部核心”)。</summary>
    private ulong AllCoresMask()
    {
        int n; try { n = _topoService.Detect().TotalLogicalProcessors; } catch { n = 0; }
        if (n <= 0) n = Environment.ProcessorCount;
        return n >= 64 ? ~0UL : (1UL << n) - 1;
    }

    /// <summary>打开“多选核心”对话框,把进程绑定到所选核心集合(复选框)。</summary>
    [RelayCommand]
    private async Task OpenAffinity(ProcessItem i)
    {
        try
        {
            var topo = _topoService.Detect();
            ulong cur;
            try
            {
                using (var proc = Process.GetProcessById(i.Pid))
                    cur = (ulong)proc.ProcessorAffinity.ToInt64();
            }
            catch { cur = AllCoresMask(); }

            var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (owner == null) return;

            bool alreadyPersisted = PersistentAffinityStore.TryGet(i.Name, out _);
            var dlg = new Views.CoreSelectDialog($"{i.Name} · CPU 核心亲和性",
                topo.TotalLogicalProcessors, topo.PcoreMask, topo.EcoreMask, cur, alreadyPersisted);
            var res = await dlg.ShowDialog<Views.CoreSelectResult?>(owner);
            if (res == null) return; // 取消

            if (res.Reset)
            {
                ManualAffinityRegistry.Remove(i.Pid);
                PersistentAffinityStore.Remove(i.Name);
                bool r = ProcessControlService.SetAffinityMask(i.Pid, AllCoresMask());
                if (Parent != null) Parent.StatusText = r ? $"已还原 {i.Name} 的 CPU 亲和性(全部核心,并清除重启保留)" : "还原失败";
                Refresh();
                return;
            }

            ulong m = res.Mask;
            if (m == 0) { if (Parent != null) Parent.StatusText = "未选择任何核心,已取消"; return; }

            bool ok = res.HardLock ? ProcessControlService.LockAffinityMask(i.Pid, m)
                                   : ProcessControlService.SetAffinityMask(i.Pid, m);
            // 记录手动选择,让守护线程按“指定核心优先”维持它、并压过规则。
            ManualAffinityRegistry.Set(i.Pid, m);
            // 重启软件后保留:按程序名记住/取消记住。
            if (res.Persist) PersistentAffinityStore.Upsert(i.Name, m, res.HardLock);
            else PersistentAffinityStore.Remove(i.Name);

            int cnt = 0; for (int b = 0; b < 64; b++) if ((m & (1UL << b)) != 0) cnt++;
            string tip = (res.HardLock ? "(硬锁)" : "") + (res.Persist ? "(重启保留)" : "");
            if (Parent != null)
                Parent.StatusText = ok ? $"已将 {i.Name} 绑定到 {cnt} 个核心 (0x{m:X}){tip}" : "设置亲和性失败(可能需要管理员)";
            Refresh();
        }
        catch (Exception ex) { Log.Error(ex, "OpenAffinity failed"); if (Parent != null) Parent.StatusText = "核心选择失败"; }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    public async void Refresh()
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        var ct = _refreshCts.Token;
        IsLoading = true; LoadError = "";
        try
        {
            var items = await Task.Run(() => EnumerateProcesses(ct), ct);
            if (ct.IsCancellationRequested) return;
            // The await continuation may run on a thread-pool thread (Refresh is
            // also invoked from background tasks) — always publish to the UI thread
            // so the ObservableCollection is only touched there.
            RunOnUi(() =>
            {
                if (ct.IsCancellationRequested) return;
                _allItems = items;
                ApplyFilter();
                IsLoading = false;
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error(ex, "Enum failed"); RunOnUi(() => { LoadError = ex.Message; IsLoading = false; }); }
    }

    private static void RunOnUi(Action action)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
                action();
            else
                Dispatcher.UIThread.Post(action);
        }
        catch { }
    }

    private void ApplyFilter()
    {
        string query = (SearchText ?? "").Trim();
        IEnumerable<ProcessItem> filtered = string.IsNullOrEmpty(query)
            ? _allItems
            : _allItems.Where(p => ProcessSearch.Matches(query, p.Name, p.Path, p.Pid));

        // FilterMode: 全部进程 / 已匹配规则 / Job 强制 — previously a dead dropdown.
        filtered = FilterMode switch
        {
            "已匹配规则" => filtered.Where(p => !string.IsNullOrEmpty(p.MatchedRule)),
            "Job 强制" => filtered.Where(p => p.RuleLevel is "job-enforced" or "job-locked"),
            _ => filtered
        };

        filtered = SortMode switch
        {
            "CPU"  => filtered.OrderByDescending(p => p.Cpu),
            "内存" => filtered.OrderByDescending(p => p.MemMb),
            "PID"  => filtered.OrderBy(p => p.Pid),
            _      => filtered.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
        };

        Processes.Clear();
        foreach (var item in filtered) Processes.Add(item);
    }

    private List<ProcessItem> EnumerateProcesses(CancellationToken ct)
    {
        var result = new List<ProcessItem>();
        Process[]? procs = null;
        try { procs = Process.GetProcesses(); } catch (Exception ex) { Log.Warning(ex, "GetProcesses failed"); return result; }
        foreach (var proc in procs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                int pid; string name;
                try { pid = proc.Id; } catch { continue; }
                try { name = proc.ProcessName + ".exe"; } catch { continue; }
                // Fast native path query instead of slow, exception-prone MainModule —
                // important now that auto-refresh can enumerate every 2 seconds.
                string? path = null; try { if (!IsSystemPid(pid)) path = EnforcementService.GetProcessPath(pid); } catch { }
                string aff = "N/A"; try { if (!IsSystemPid(pid)) aff = $"0x{proc.ProcessorAffinity.ToInt64():X}"; } catch { }

                long mem = 0; try { mem = proc.WorkingSet64; } catch { }
                double cpu = 0;
                try
                {
                    var tp = proc.TotalProcessorTime;
                    long now = Environment.TickCount64;
                    if (_prevCpu.TryGetValue(pid, out var prev))
                    {
                        double dt = now - prev.tick;
                        if (dt > 0)
                        {
                            cpu = (tp - prev.cpu).TotalMilliseconds / (dt * Environment.ProcessorCount) * 100.0;
                            if (cpu < 0) cpu = 0; if (cpu > 100) cpu = 100;
                        }
                    }
                    _prevCpu[pid] = (tp, now);
                }
                catch { }

                var rule = _ruleEngine.Match(name, path ?? "");
                result.Add(new ProcessItem { Pid = pid, Name = name, Path = path ?? "(protected)", Affinity = aff, MatchedRule = rule?.Name ?? "", RuleLevel = rule?.Action.Level ?? "", MemMb = mem / 1024 / 1024, Cpu = cpu });
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            finally { try { proc.Dispose(); } catch { } }
        }

        // Drop CPU-sampling state for processes that no longer exist.
        try
        {
            var live = new HashSet<int>();
            foreach (var r in result) live.Add(r.Pid);
            foreach (var k in _prevCpu.Keys.ToList())
                if (!live.Contains(k)) _prevCpu.Remove(k);
        }
        catch { }

        return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Pid).ToList();
    }

    private static bool IsSystemPid(int pid) => pid is 0 or 4;

    // ── Context menu / inline actions ──

    [RelayCommand] private void SetAffinityPCores(ProcessItem item) => ApplyQuick(item, "p-cores|first-half", "hard-affinity");
    [RelayCommand] private void SetAffinityECores(ProcessItem item) => ApplyQuick(item, "e-cores|second-half", "hard-affinity");
    [RelayCommand] private void SetAffinityAllCores(ProcessItem item) => ApplyQuick(item, "all-cores", "hard-affinity");
    [RelayCommand] private void SetAffinityFirstHalf(ProcessItem item) => ApplyQuick(item, "first-half", "hard-affinity");
    [RelayCommand] private void SetAffinitySecondHalf(ProcessItem item) => ApplyQuick(item, "second-half", "hard-affinity");
    [RelayCommand] private void SetJobEnforced(ProcessItem item) => ApplyQuick(item, "p-cores|all-cores", "job-enforced");
    [RelayCommand] private void SetJobLocked(ProcessItem item) => ApplyQuick(item, "all-cores", "job-locked");

    [RelayCommand]
    private void ApplyMatchedRule(ProcessItem item)
    {
        try
        {
            var rule = _ruleEngine.Match(item.Name, item.Path);
            if (rule != null)
            {
                _enforcementService.Apply(item.Pid, rule, _topoService.Detect());
                if (Parent != null) Parent.StatusText = $"已对 PID {item.Pid} 应用规则『{rule.Name}』";
            }
        }
        catch (Exception ex) { Log.Error(ex, "Apply matched rule failed"); }
    }

    // ── 专业版：进程动作 / 优先级 / 内存 / 信息 ──

    [RelayCommand] private void SuspendProcess(ProcessItem i) => Do(i, () => ProcessControlService.Suspend(i.Pid), $"已挂起 PID {i.Pid}", $"挂起 PID {i.Pid} 失败");
    [RelayCommand] private void ResumeProcess(ProcessItem i) => Do(i, () => ProcessControlService.Resume(i.Pid), $"已恢复 PID {i.Pid}", $"恢复 PID {i.Pid} 失败");
    [RelayCommand] private void TerminateProcess(ProcessItem i) => Do(i, () => ProcessControlService.Terminate(i.Pid), $"已结束 PID {i.Pid}", $"结束 PID {i.Pid} 失败", refresh: true);
    [RelayCommand] private void TerminateTree(ProcessItem i) => Do(i, () => ProcessControlService.TerminateTree(i.Pid), $"已结束进程树 PID {i.Pid}", $"结束进程树 PID {i.Pid} 失败", refresh: true);

    [RelayCommand]
    private async Task CopyInfo(ProcessItem i)
    {
        try
        {
            string text = $"{i.Name}\nPID: {i.Pid}\n路径: {i.Path}\n亲和性: {i.Affinity}";
            var top = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            var cb = top?.Clipboard;
            if (cb != null)
            {
                await cb.SetTextAsync(text);
                if (Parent != null) Parent.StatusText = $"已复制 {i.Name} 的信息到剪贴板";
            }
        }
        catch (Exception ex) { Log.Error(ex, "copy failed"); }
    }

    // CPU 优先级
    [RelayCommand] private void PriorityHigh(ProcessItem i) => Do(i, () => ProcessControlService.SetPriority(i.Pid, ProcessPriorityClass.High), $"PID {i.Pid} CPU 优先级→高", "设置失败");
    [RelayCommand] private void PriorityNormal(ProcessItem i) => Do(i, () => ProcessControlService.SetPriority(i.Pid, ProcessPriorityClass.Normal), $"PID {i.Pid} CPU 优先级→常规", "设置失败");
    [RelayCommand] private void PriorityIdle(ProcessItem i) => Do(i, () => ProcessControlService.SetPriority(i.Pid, ProcessPriorityClass.Idle), $"PID {i.Pid} CPU 优先级→低", "设置失败");

    // GPU 优先级(WDDM 调度优先级,独立于 CPU 优先级) 5=实时 4=高 2=常规 0=空闲
    [RelayCommand] private void GpuPriorityRealtime(ProcessItem i) => Do(i, () => ProcessControlService.SetGpuPriority(i.Pid, 5), $"PID {i.Pid} GPU 优先级→实时", "设置失败(需管理员,且依赖显卡驱动支持)");
    [RelayCommand] private void GpuPriorityHigh(ProcessItem i) => Do(i, () => ProcessControlService.SetGpuPriority(i.Pid, 4), $"PID {i.Pid} GPU 优先级→高", "设置失败(需管理员,且依赖显卡驱动支持)");
    [RelayCommand] private void GpuPriorityNormal(ProcessItem i) => Do(i, () => ProcessControlService.SetGpuPriority(i.Pid, 2), $"PID {i.Pid} GPU 优先级→常规", "设置失败");
    [RelayCommand] private void GpuPriorityLow(ProcessItem i) => Do(i, () => ProcessControlService.SetGpuPriority(i.Pid, 0), $"PID {i.Pid} GPU 优先级→低", "设置失败");

    [RelayCommand] private void IoPriorityLow(ProcessItem i) => Do(i, () => ProcessControlService.SetIoPriority(i.Pid, 1), $"PID {i.Pid} IO 优先级→低", "设置失败");
    [RelayCommand] private void IoPriorityNormal(ProcessItem i) => Do(i, () => ProcessControlService.SetIoPriority(i.Pid, 2), $"PID {i.Pid} IO 优先级→常规", "设置失败");
    [RelayCommand] private void IoPriorityHigh(ProcessItem i) => Do(i, () => ProcessControlService.SetIoPriority(i.Pid, 3), $"PID {i.Pid} IO 优先级→高", "设置失败(高 IO 优先级通常需管理员)");
    [RelayCommand] private void MemoryPriorityLow(ProcessItem i) => Do(i, () => ProcessControlService.SetMemoryPriority(i.Pid, 1), $"PID {i.Pid} 内存优先级→低", "设置失败");
    [RelayCommand] private void MemoryPriorityNormal(ProcessItem i) => Do(i, () => ProcessControlService.SetMemoryPriority(i.Pid, 3), $"PID {i.Pid} 内存优先级→常规", "设置失败");
    [RelayCommand] private void MemoryPriorityHigh(ProcessItem i) => Do(i, () => ProcessControlService.SetMemoryPriority(i.Pid, 5), $"PID {i.Pid} 内存优先级→高(系统最高档)", "设置失败");
    [RelayCommand] private void EfficiencyOn(ProcessItem i) => Do(i, () => ProcessControlService.SetEfficiencyMode(i.Pid, true), $"PID {i.Pid} 已开启效率模式", "设置失败");
    [RelayCommand] private void EfficiencyOff(ProcessItem i) => Do(i, () => ProcessControlService.SetEfficiencyMode(i.Pid, false), $"PID {i.Pid} 已关闭效率模式", "设置失败");
    [RelayCommand] private void EmptyWorkingSet(ProcessItem i) => Do(i, () => ProcessControlService.EmptyWorkingSet(i.Pid), $"PID {i.Pid} 已释放物理内存", "释放失败");
    [RelayCommand] private void LimitMem512(ProcessItem i) => Do(i, () => ProcessControlService.LimitWorkingSet(i.Pid, 0, 512), $"PID {i.Pid} 物理内存上限→512MB", "设置失败");
    [RelayCommand] private void LimitMem1024(ProcessItem i) => Do(i, () => ProcessControlService.LimitWorkingSet(i.Pid, 0, 1024), $"PID {i.Pid} 物理内存上限→1GB", "设置失败");
    [RelayCommand] private void UnlimitMem(ProcessItem i) => Do(i, () => ProcessControlService.LimitWorkingSet(i.Pid, 0, 0), $"PID {i.Pid} 已解除物理内存上限", "设置失败");
    [RelayCommand] private void LimitVMem512(ProcessItem i) => Do(i, () => ProcessControlService.LimitVirtualMemory(i.Pid, 512), $"PID {i.Pid} 虚拟内存(提交)上限→512MB", "设置失败(进程可能已在其它 Job 中)");
    [RelayCommand] private void LimitVMem1024(ProcessItem i) => Do(i, () => ProcessControlService.LimitVirtualMemory(i.Pid, 1024), $"PID {i.Pid} 虚拟内存(提交)上限→1GB", "设置失败");
    [RelayCommand] private void LimitVMem2048(ProcessItem i) => Do(i, () => ProcessControlService.LimitVirtualMemory(i.Pid, 2048), $"PID {i.Pid} 虚拟内存(提交)上限→2GB", "设置失败");

    // 右键某行时由 ProcessListView 记录 ContextItem,供“核心分配算法”动态子菜单点击时对该进程生效。
    public ProcessItem? ContextItem { get; set; }

    // ── 各分类的“还原默认”动作 ──
    [RelayCommand] private void ResetCpuPriority(ProcessItem i) => Do(i, () => { ProcessControlService.SetPriorityBoost(i.Pid, true); return ProcessControlService.SetPriority(i.Pid, ProcessPriorityClass.Normal); }, $"PID {i.Pid} CPU 优先级已还原(常规)", "还原失败");
    [RelayCommand] private void ResetGpuPriority(ProcessItem i) => Do(i, () => ProcessControlService.SetGpuPriority(i.Pid, 2), $"PID {i.Pid} GPU 优先级已还原(常规)", "还原失败");
    [RelayCommand] private void ResetIoPriority(ProcessItem i) => Do(i, () => ProcessControlService.SetIoPriority(i.Pid, 2), $"PID {i.Pid} IO 优先级已还原(常规)", "还原失败");
    [RelayCommand] private void ResetMemoryPriority(ProcessItem i) => Do(i, () => ProcessControlService.SetMemoryPriority(i.Pid, 5), $"PID {i.Pid} 内存优先级已还原(系统默认)", "还原失败");
    [RelayCommand] private void ResetAffinity(ProcessItem i) => Do(i, () => { ManualAffinityRegistry.Remove(i.Pid); PersistentAffinityStore.Remove(i.Name); return ProcessControlService.SetAffinityMask(i.Pid, AllCoresMask()); }, $"已还原 {i.Name} 的 CPU 亲和性(全部核心,并清除重启保留)", "还原失败", refresh: true);
    [RelayCommand] private void ResetCoreAlgorithm(ProcessItem i) => Do(i, () => ProcessControlService.ResetThreadScheduling(i.Pid, AllCoresMask()), $"已还原 {i.Name} 的线程调度(清除绑定/提权)", "还原失败", refresh: true);

    [RelayCommand]
    private void ThreadSchedule(ProcessItem i) => Do(i, () =>
    {
        var topo = _topoService.Detect();
        ulong mask = CpuTopology.BuildMask(topo, "p-cores|all-cores");
        return ProcessControlService.DistributeThreads(i.Pid, mask);
    }, $"已对 {i.Name} 做线程级核心调度(线程分散到各核)", "线程调度失败");

    [RelayCommand] private void BringToFront(ProcessItem i) => Do(i, () => ProcessControlService.BringToFront(i.Pid), $"已置顶 {i.Name}", "该进程无主窗口");
    [RelayCommand] private void MinimizeWindow(ProcessItem i) => Do(i, () => ProcessControlService.MinimizeWindow(i.Pid), $"已最小化 {i.Name}", "该进程无主窗口");
    [RelayCommand] private void RestoreWindow(ProcessItem i) => Do(i, () => ProcessControlService.RestoreWindow(i.Pid), $"已还原 {i.Name}", "该进程无主窗口");

    [RelayCommand]
    private void LocateFile(ProcessItem i)
    {
        try
        {
            if (!string.IsNullOrEmpty(i.Path) && i.Path.Contains(':'))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{i.Path}\"") { UseShellExecute = true });
                if (Parent != null) Parent.StatusText = $"已在资源管理器定位 {i.Name}";
            }
            else if (Parent != null) Parent.StatusText = "该进程路径不可用(受保护)";
        }
        catch (Exception ex) { Log.Error(ex, "locate failed"); }
    }

    [RelayCommand]
    private void ShowConnections(ProcessItem i)
    {
        try
        {
            var s = ProcessInfoService.GetSummary(i.Pid);
            string net = s.Connections.Count == 0 ? "无网络连接"
                : $"{s.Connections.Count} 条连接:{string.Join(" | ", s.Connections.Take(4))}{(s.Connections.Count > 4 ? " …" : "")}";
            if (Parent != null)
                Parent.StatusText = $"{i.Name}(PID {i.Pid}) · 完整性 {s.IntegrityLevel} · 句柄 {s.Handles} · 线程 {s.Threads} · 内存 {s.WorkingSetBytes / 1024 / 1024}MB · {net}";
        }
        catch (Exception ex) { Log.Error(ex, "info failed"); }
    }

    /// <summary>
    /// Shows the process's thread-level scheduling summary in the status bar:
    /// how many threads are pinned to a restricted core set, plus the main thread's
    /// ideal processor. Full per-thread affinity control is available through the
    /// HTTP API (/api/processes/{pid}/threads) and MCP tools (list_threads,
    /// set_thread_affinity).
    /// </summary>
    [RelayCommand]
    private void ShowThreads(ProcessItem i)
    {
        try
        {
            var threads = ProcessControlService.GetThreads(i.Pid);
            if (threads.Count == 0)
            {
                if (Parent != null) Parent.StatusText = $"无法读取 {i.Name} 的线程信息(可能已退出或拒绝访问)";
                return;
            }

            int total = _topoService.Detect().TotalLogicalProcessors;
            ulong allMask = total >= 64 ? ~0UL : ((1UL << total) - 1);
            int restricted = threads.Count(t => t.AffinityMask != 0 && t.AffinityMask != allMask);
            var main = threads.FirstOrDefault(t => t.IsMainThread);
            string mainInfo = main == null
                ? "主线程未知"
                : $"主线程#{main.Tid} 理想核 {(main.IdealProcessor >= 0 ? main.IdealProcessor.ToString() : "系统默认")} 亲和 0x{main.AffinityMask:X}";
            var busiest = threads.OrderByDescending(t => t.TotalCpuMs).FirstOrDefault();
            string busyInfo = busiest == null
                ? ""
                : $" · 最忙线程#{busiest.Tid} 已用 {busiest.TotalCpuMs / 1000.0:0.0}s";

            if (Parent != null)
                Parent.StatusText = $"{i.Name}(PID {i.Pid}) · 共 {threads.Count} 线程 · {restricted} 个被限制核心 · {mainInfo}{busyInfo}";
        }
        catch (Exception ex) { Log.Error(ex, "threads info failed"); }
    }

    private void Do(ProcessItem i, Func<bool> action, string ok, string fail, bool refresh = false)
    {
        try
        {
            bool r = action();
            if (Parent != null) Parent.StatusText = r ? ok : fail;
            if (refresh) Refresh();
        }
        catch (Exception ex) { Log.Error(ex, "process action failed"); if (Parent != null) Parent.StatusText = fail; }
    }

    private void ApplyQuick(ProcessItem item, string mode, string level)
    {
        try
        {
            var topo = _topoService.Detect();
            var rule = new RuleEntry { Id = "quick", Name = "Quick Action", Action = new RuleAction { Mode = mode, Level = level } };
            bool ok = _enforcementService.Apply(item.Pid, rule, topo);
            if (Parent != null) Parent.StatusText = ok ? $"已对 PID {item.Pid} 应用 {mode} [{level}]" : $"PID {item.Pid} 应用失败";
        }
        catch (Exception ex) { Log.Error(ex, "Quick apply failed"); }
    }
}

public partial class ProcessItem : ObservableObject
{
    [ObservableProperty] private int _pid;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _path = "";
    [ObservableProperty] private string _affinity = "";
    [ObservableProperty] private string _matchedRule = "";
    [ObservableProperty] private string _ruleLevel = "";
    [ObservableProperty] private double _cpu;
    [ObservableProperty] private long _memMb;

    public string CpuText => $"CPU {Cpu:0.0}%";
    public string MemText => $"{MemMb} MB";

    partial void OnCpuChanged(double value) => OnPropertyChanged(nameof(CpuText));
    partial void OnMemMbChanged(long value) => OnPropertyChanged(nameof(MemText));
}

/// <summary>“优先跑指定核心”子菜单的一项:显示文本 + 点击命令。</summary>
public sealed class CoreChoice
{
    public CoreChoice(string label, IRelayCommand command) { Label = label; Command = command; }
    public string Label { get; }
    public IRelayCommand Command { get; }
}
