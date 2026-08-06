using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CpuAffinityManager.Cpu;
using CpuAffinityManager.Gpu;
using CpuAffinityManager.ProcOps;

namespace CpuAffinityManager.Avalonia.ViewModels;

/// <summary>
/// System-wide "professional" tools: memory cleanup, timer resolution, power plan,
/// and foreground priority separation. Shows the CURRENT effective state so every
/// choice has visible feedback (highlight + status line). Most are global and need admin.
/// </summary>
public partial class SystemToolsViewModel : ViewModelBase
{
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;

    // ── 当前生效状态(选择反馈)──

    /// <summary>当前激活电源计划名称。</summary>
    [ObservableProperty] private string _currentPowerPlan = "查询中…";

    /// <summary>电源计划按钮高亮索引(-1=无匹配)。</summary>
    [ObservableProperty] private int _selectedPowerPlanIndex = -1;

    /// <summary>当前计时器分辨率。</summary>
    [ObservableProperty] private string _currentTimerMs = "查询中…";

    /// <summary>计时器按钮高亮索引(0=0.5ms, 1=1.0ms, 2=15.6ms)。</summary>
    [ObservableProperty] private int _selectedTimerIndex = -1;

    /// <summary>前台加速是否生效。</summary>
    [ObservableProperty] private bool _foregroundAccelActive;

    // ── 一键优化/成对操作:高亮跟随"上次选中的选项" ──

    [ObservableProperty] private bool _gameModeActive;
    [ObservableProperty] private bool _balancedModeActive;
    [ObservableProperty] private bool _powerSaveActive;
    [ObservableProperty] private bool _graphicsActive;

    /// <summary>当前生效的一键优化模式名称(状态总览显示)。</summary>
    [ObservableProperty] private string _currentPresetName = "未应用";

    /// <summary>MMCSS 是否已优化(高亮"优化/恢复默认")。</summary>
    [ObservableProperty] private bool _mmcssOptimized;

    /// <summary>低延迟优化是否已应用。</summary>
    [ObservableProperty] private bool _latencyOptimized;

    /// <summary>是否正在防睡眠/息屏。</summary>
    [ObservableProperty] private bool _wakeLockActive;

    /// <summary>CPU 摘要(品牌/型号/线程数)。</summary>
    [ObservableProperty] private string _cpuInfo = "";

    /// <summary>显卡列表摘要。</summary>
    [ObservableProperty] private string _gpuSummary = "";

    public ObservableCollection<string> GpuList { get; } = new();

    public static string[] PowerPlans { get; } = ["平衡", "高性能", "节能", "卓越性能"];

    public SystemToolsViewModel()
    {
        RefreshStatus();
    }

    /// <summary>重新读取系统当前生效项(电源计划/计时器/优先级/GPU)。</summary>
    public void RefreshStatus()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            // 电源计划
            string plan = SystemTweaks.GetCurrentPowerPlanName();
            CurrentPowerPlan = plan;
            SelectedPowerPlanIndex = plan switch
            {
                "平衡" => 0,
                "高性能" => 1,
                "节能" => 2,
                _ when plan.Contains("卓越", StringComparison.Ordinal) => 3,
                _ => -1
            };

            // 计时器分辨率
            double ms = SystemTweaks.GetTimerResolutionMs();
            if (ms < 0) CurrentTimerMs = "未知";
            else
            {
                CurrentTimerMs = $"{ms:0.00} ms";
                SelectedTimerIndex = ms <= 0.7 ? 0 : ms <= 1.3 ? 1 : 2;
            }

            // 前台加速(38=前台加速, 2=默认)
            ForegroundAccelActive = SystemTweaks.GetPrioritySeparation() == 38;

            // CPU 信息
            try
            {
                var topo = new CpuTopologyService().Detect();
                CpuInfo = string.IsNullOrWhiteSpace(topo.CpuModelName)
                    ? $"{topo.TotalLogicalProcessors} 线程"
                    : topo.CpuModelName.Trim() + $" · {topo.TotalLogicalProcessors} 线程" + (topo.IsHybrid ? $" · {topo.PcoreCount}P+{topo.EcoreCount}E" : "");
            }
            catch { CpuInfo = "检测失败"; }

            // GPU 信息
            GpuList.Clear();
            var gpus = GpuInfoService.Enumerate();
            foreach (var g in gpus) GpuList.Add(g.Name);
            GpuSummary = gpus.Count == 0
                ? "未检测到显卡"
                : string.Join(" / ", gpus.Select(g => $"{g.Name}({g.RamText})"));
        }
        catch { }
    }

    // ── 一键优化预设(组合调用现有系统调优)──

    [RelayCommand]
    private async Task GameModeAsync()
    {
        if (!OperatingSystem.IsWindows()) { Status = "仅 Windows 可用"; return; }
        Status = "正在应用游戏模式…";
        Status = await Task.Run(() =>
        {
            string a = SystemTweaks.SetPowerPlan(PowerPlan.HighPerformance);
            string b = SystemTweaks.SetTimerResolution(0.5);
            string c = SystemTweaks.SetPrioritySeparation(38); // 前台加速
            return "游戏模式已应用 · " + a.Split('·', ',')[^1].Trim();
        });
        SetPreset(0);
        RefreshStatus();
    }

    [RelayCommand]
    private async Task BalancedModeAsync()
    {
        if (!OperatingSystem.IsWindows()) { Status = "仅 Windows 可用"; return; }
        Status = "正在应用均衡模式…";
        Status = await Task.Run(() =>
        {
            SystemTweaks.SetPowerPlan(PowerPlan.Balanced);
            SystemTweaks.SetTimerResolution(15.6);
            SystemTweaks.SetPrioritySeparation(2);
            return "均衡模式已应用(平衡电源 + 默认计时器 + 默认前台)";
        });
        SetPreset(1);
        RefreshStatus();
    }

    [RelayCommand]
    private async Task GraphicsPresetAsync()
    {
        if (!OperatingSystem.IsWindows()) { Status = "仅 Windows 可用"; return; }
        Status = "正在应用图形性能优化…";
        Status = await Task.Run(() => SystemTweaks.ApplyGraphicsPreset());
        SetPreset(3);
        RefreshStatus();
    }

    [RelayCommand]
    private void MmcssOptimize()
    {
        if (!OperatingSystem.IsWindows()) { Status = "仅 Windows 可用"; return; }
        Status = SystemTweaks.ApplyMmcss(true);
        MmcssOptimized = Status.StartsWith("已应用", StringComparison.Ordinal);
    }

    [RelayCommand]
    private void MmcssReset()
    {
        if (!OperatingSystem.IsWindows()) { Status = "仅 Windows 可用"; return; }
        Status = SystemTweaks.ApplyMmcss(false);
        MmcssOptimized = false;
    }

    // ── DPC 延迟 / 响应速度优化 ──

    [RelayCommand]
    private async Task LatencyOptimizeAsync()
    {
        if (!OperatingSystem.IsWindows()) { Status = "仅 Windows 可用"; return; }
        Status = "正在应用低延迟/高响应优化…";
        Status = await Task.Run(() => SystemTweaks.ApplyLatencyOptimization(true));
        LatencyOptimized = Status.StartsWith("已应用", StringComparison.Ordinal);
        RefreshStatus();
    }

    [RelayCommand]
    private async Task LatencyResetAsync()
    {
        if (!OperatingSystem.IsWindows()) { Status = "仅 Windows 可用"; return; }
        Status = "正在恢复默认延迟/响应设置…";
        Status = await Task.Run(() => SystemTweaks.ApplyLatencyOptimization(false));
        LatencyOptimized = false;
        RefreshStatus();
    }

    [RelayCommand]
    private async Task PowerSaveModeAsync()
    {
        if (!OperatingSystem.IsWindows()) { Status = "仅 Windows 可用"; return; }
        Status = "正在应用省电模式…";
        Status = await Task.Run(() =>
        {
            SystemTweaks.SetPowerPlan(PowerPlan.PowerSaver);
            SystemTweaks.SetTimerResolution(15.6);
            SystemTweaks.SetPrioritySeparation(24);
            return "省电模式已应用(节能电源 + 默认计时器 + 后台均衡)";
        });
        SetPreset(2);
        RefreshStatus();
    }

    [RelayCommand]
    private async Task CleanMemoryAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "正在清理系统内存…";
        try
        {
            int n = await Task.Run(() => OperatingSystem.IsWindows() ? SystemTweaks.CleanSystemMemory() : 0);
            Status = $"已清理 {n} 个进程的工作集";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ClearFileCacheAsync()
    {
        if (!OperatingSystem.IsWindows()) { Status = "仅 Windows 可用"; return; }
        Status = await Task.Run(() => SystemTweaks.ClearSystemFileCache());
    }

    [RelayCommand]
    private async Task CleanPowerPlansAsync()
    {
        if (!OperatingSystem.IsWindows()) { Status = "仅 Windows 可用"; return; }
        Status = "正在清理多余电源计划…";
        Status = await Task.Run(() => SystemTweaks.CleanDuplicatePowerPlans());
        RefreshStatus();
    }

    // ── 睡眠 / 亮度 / 快捷入口 / DNS ──

    [RelayCommand]
    private void PreventSleep()
    {
        if (!OperatingSystem.IsWindows()) { Status = "仅 Windows 可用"; return; }
        Status = SystemTweaks.SetSleepPrevention(true);
        WakeLockActive = Status.StartsWith("已防止", StringComparison.Ordinal);
    }

    [RelayCommand]
    private void AllowSleep()
    {
        if (!OperatingSystem.IsWindows()) { Status = "仅 Windows 可用"; return; }
        Status = SystemTweaks.SetSleepPrevention(false);
        WakeLockActive = false;
    }

    [RelayCommand]
    private void SleepNow()
    {
        if (!OperatingSystem.IsWindows()) { Status = "仅 Windows 可用"; return; }
        Status = SystemTweaks.SleepNow();
    }

    [RelayCommand]
    private void SetBrightness(string percent)
    {
        if (!OperatingSystem.IsWindows()) { Status = "仅 Windows 可用"; return; }
        if (int.TryParse(percent, out int p)) Status = SystemTweaks.SetBrightness(p);
    }

    [RelayCommand]
    private async Task SetDnsAsync(string preset)
    {
        if (!OperatingSystem.IsWindows()) { Status = "仅 Windows 可用"; return; }
        Status = "正在设置 DNS…";
        Status = await Task.Run(() => SystemTweaks.SetDns(preset));
    }

    [RelayCommand]
    private async Task SetMtuAsync(string mtu)
    {
        if (!OperatingSystem.IsWindows()) { Status = "仅 Windows 可用"; return; }
        if (!int.TryParse(mtu, out int v)) return;
        Status = "正在设置 MTU…";
        Status = await Task.Run(() => SystemTweaks.SetMtu(v));
    }

    [RelayCommand]
    private async Task CleanStandbyAsync()
    {
        if (!OperatingSystem.IsWindows()) { Status = "仅 Windows 可用"; return; }
        Status = "正在清理待机内存…";
        Status = await Task.Run(() => SystemTweaks.CleanStandbyList());
    }

    [RelayCommand]
    private void OpenTool(string which)
    {
        try
        {
            Process.Start(new ProcessStartInfo(which) { UseShellExecute = true });
            Status = "已打开:" + which;
        }
        catch (Exception ex) { Status = "无法打开 " + which + ":" + ex.Message; }
    }

    [RelayCommand]
    private async Task ApplyTimerAsync(string ms)
    {
        if (!double.TryParse(ms, out double v)) { Status = "计时器数值无效"; return; }
        if (!OperatingSystem.IsWindows()) { Status = "仅 Windows 可用"; return; }
        Status = await Task.Run(() => SystemTweaks.SetTimerResolution(v));
        RefreshStatus();
    }

    [RelayCommand]
    private async Task SetPowerAsync(string plan)
    {
        if (!OperatingSystem.IsWindows()) { Status = "仅 Windows 可用"; return; }
        var p = plan switch
        {
            "高性能" => PowerPlan.HighPerformance,
            "节能" => PowerPlan.PowerSaver,
            "卓越性能" => PowerPlan.UltimatePerformance,
            _ => PowerPlan.Balanced
        };
        Status = "正在切换电源模式…";
        Status = await Task.Run(() => SystemTweaks.SetPowerPlan(p));
        RefreshStatus();
    }

    [RelayCommand]
    private async Task SetPrioritySepAsync(string value)
    {
        if (!OperatingSystem.IsWindows()) { Status = "仅 Windows 可用"; return; }
        if (!int.TryParse(value, out int v)) return;
        Status = await Task.Run(() => SystemTweaks.SetPrioritySeparation(v));
        RefreshStatus();
    }

    /// <summary>切换一键优化模式高亮:0=游戏, 1=均衡, 2=省电, 3=图形优化。</summary>
    private void SetPreset(int index)
    {
        GameModeActive = index == 0;
        BalancedModeActive = index == 1;
        PowerSaveActive = index == 2;
        GraphicsActive = index == 3;
        CurrentPresetName = index switch
        {
            0 => "游戏模式",
            1 => "均衡模式",
            2 => "省电模式",
            _ => "图形性能优化"
        };
    }
}
