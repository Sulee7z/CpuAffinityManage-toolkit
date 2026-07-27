using System;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CpuAffinityManager.ProcOps;

namespace CpuAffinityManager.Avalonia.ViewModels;

/// <summary>
/// System-wide "professional" tools: memory cleanup, timer resolution, power plan,
/// and foreground priority separation. Most of these are global and need admin.
/// </summary>
public partial class SystemToolsViewModel : ViewModelBase
{
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;

    public static string[] PowerPlans { get; } = ["平衡", "高性能", "节能", "卓越性能"];

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
    }

    [RelayCommand]
    private async Task GraphicsPresetAsync()
    {
        if (!OperatingSystem.IsWindows()) { Status = "仅 Windows 可用"; return; }
        Status = "正在应用图形性能优化…";
        Status = await Task.Run(() => SystemTweaks.ApplyGraphicsPreset());
    }

    [RelayCommand]
    private void MmcssOptimize()
    {
        if (!OperatingSystem.IsWindows()) { Status = "仅 Windows 可用"; return; }
        Status = SystemTweaks.ApplyMmcss(true);
    }

    [RelayCommand]
    private void MmcssReset()
    {
        if (!OperatingSystem.IsWindows()) { Status = "仅 Windows 可用"; return; }
        Status = SystemTweaks.ApplyMmcss(false);
    }

    // ── DPC 延迟 / 响应速度优化 ──

    [RelayCommand]
    private async Task LatencyOptimizeAsync()
    {
        if (!OperatingSystem.IsWindows()) { Status = "仅 Windows 可用"; return; }
        Status = "正在应用低延迟/高响应优化…";
        Status = await Task.Run(() => SystemTweaks.ApplyLatencyOptimization(true));
    }

    [RelayCommand]
    private async Task LatencyResetAsync()
    {
        if (!OperatingSystem.IsWindows()) { Status = "仅 Windows 可用"; return; }
        Status = "正在恢复默认延迟/响应设置…";
        Status = await Task.Run(() => SystemTweaks.ApplyLatencyOptimization(false));
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
    }

    // ── 睡眠 / 亮度 / 快捷入口 / DNS ──

    [RelayCommand]
    private void PreventSleep()
    {
        if (!OperatingSystem.IsWindows()) { Status = "仅 Windows 可用"; return; }
        Status = SystemTweaks.SetSleepPrevention(true);
    }

    [RelayCommand]
    private void AllowSleep()
    {
        if (!OperatingSystem.IsWindows()) { Status = "仅 Windows 可用"; return; }
        Status = SystemTweaks.SetSleepPrevention(false);
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
    }

    [RelayCommand]
    private async Task SetPrioritySepAsync(string value)
    {
        if (!OperatingSystem.IsWindows()) { Status = "仅 Windows 可用"; return; }
        if (!int.TryParse(value, out int v)) return;
        Status = await Task.Run(() => SystemTweaks.SetPrioritySeparation(v));
    }
}
