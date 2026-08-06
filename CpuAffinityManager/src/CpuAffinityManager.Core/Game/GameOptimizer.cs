using CpuAffinityManager.Cpu;
using CpuAffinityManager.Engine;
using CpuAffinityManager.Gpu;

namespace CpuAffinityManager.Game;

/// <summary>
/// 把游戏优化预设应用到进程:生成一条该进程的规则(由 watchdog 持续维护),
/// 立即执行优先级/IO/防篡改设置,并按预设写入 GPU 偏好(等效绑独显)。
/// </summary>
public static class GameOptimizer
{
    /// <summary>
    /// 为进程生成预设规则并应用全部优化(含 GPU 偏好)。
    /// 规则 ID 固定为 gpreset-{exe名},重复应用同预设会覆盖更新,
    /// 切换到其他预设会替换,移除预设则删除规则。
    /// </summary>
    public static RuleEntry BuildRule(string exeName, GamePreset preset, CpuTopology topology)
    {
        string coreMask = preset.Cores == "all" ? "all" : "0x" + ResolveMask(preset.Cores, topology).ToString("X");

        // GPU 维度:游戏进程偏好高性能 GPU(独显),切换/移除预设时清理旧偏好。
        GpuInfoService.SetProcessGpuPreference(exeName, preset.GpuPreference);

        return new RuleEntry
        {
            Id = RuleIdFor(exeName),
            Name = $"🎮 {preset.Name}",
            Enabled = true,
            Match = new RuleMatch { Process = exeName },
            Action = new RuleAction
            {
                Mode = "all-cores",
                Level = preset.JobEnforce ? "job-enforced" : "hard-affinity",
                Lock = preset.JobEnforce,
                CpuPriority = preset.CpuPriority,
                IoPriority = preset.IoPriority,
                MemoryPriority = preset.MemoryPriority,
                EfficiencyMode = preset.EfficiencyMode,
                PreferredCores = coreMask,
                PreferMode = "dynamic"
            }
        };
    }

    /// <summary>移除某进程的游戏预设规则,并清除其 GPU 偏好。</summary>
    public static string RuleIdFor(string exeName)
        => "gpreset-" + exeName.Replace(".exe", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();

    /// <summary>移除游戏预设时清理 GPU 偏好(恢复系统默认)。</summary>
    public static void Cleanup(string exeName)
        => GpuInfoService.ClearProcessGpuPreference(exeName);

    private static ulong ResolveMask(string cores, CpuTopology topo)
        => GamePreset.ResolveCoreMask(cores, topo);
}
