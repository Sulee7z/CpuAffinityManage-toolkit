using CpuAffinityManager.Cpu;

namespace CpuAffinityManager.Game;

/// <summary>
/// 游戏优化预设 —— 针对不同游戏类型的核心分配/优先级组合方案。
/// 每个预设包含:核心分配策略、CPU/IO/内存优先级、防篡改锁定等,
/// 一键应用到某进程(生成一条该进程的规则,由 watchdog 持续维护)。
/// </summary>
public sealed class GamePreset
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";

    /// <summary>"best" = 自动选最优核心(P核优先);"all" = 不钉单核(全核吞吐);或 hex 掩码</summary>
    public string Cores { get; init; } = "best";

    /// <summary>CPU 优先级: realtime / high / aboveNormal / normal / belowNormal</summary>
    public string? CpuPriority { get; init; }

    /// <summary>IO 优先级: high / normal / low</summary>
    public string? IoPriority { get; init; }

    /// <summary>内存优先级: 1-5</summary>
    public int? MemoryPriority { get; init; }

    /// <summary>防篡改: true = Job 对象强制,游戏/反作弊无法改回亲和性</summary>
    public bool JobEnforce { get; init; }

    /// <summary>效率模式(EcoQoS): 省电场景</summary>
    public bool EfficiencyMode { get; init; }

    /// <summary>
    /// GPU 偏好(等效"把游戏绑到独显"): high=高性能独显, powerSaving=核显, auto=系统默认。
    /// 通过 DirectX UserGpuPreferences 按进程生效。
    /// </summary>
    public Gpu.GpuPreference GpuPreference { get; init; } = Gpu.GpuPreference.HighPerformance;

    /// <summary>内置预设库(按游戏场景)</summary>
    public static readonly GamePreset[] Catalog =
    {
        new()
        {
            Id = "esports", Name = "⚡ 电竞低延迟(CS/瓦洛兰特/LOL/永劫)",
            Description = "主线程钉最优核心,高优先级+高IO,防篡改锁定+独显 —— 帧延迟最低",
            Cores = "best", CpuPriority = "high", IoPriority = "high", MemoryPriority = 5, JobEnforce = true,
            GpuPreference = Gpu.GpuPreference.HighPerformance
        },
        new()
        {
            Id = "fps-max", Name = "🚀 极致帧率(所有FPS)",
            Description = "最优核心钉主线程 + 最高优先级 + 独显,尽一切可能压低帧延迟(建议配置好后不再调)",
            Cores = "best", CpuPriority = "realtime", IoPriority = "high", JobEnforce = true,
            GpuPreference = Gpu.GpuPreference.HighPerformance
        },
        new()
        {
            Id = "aaa", Name = "🎮 3A 单机大作(黑神话/赛博朋克/老头环)",
            Description = "主线程优先核心 + 多线程全核铺满,高优先级 + 独显 —— 帧率与画质兼顾",
            Cores = "best", CpuPriority = "high", GpuPreference = Gpu.GpuPreference.HighPerformance
        },
        new()
        {
            Id = "emulator", Name = "🕹 模拟器(Yuzu/Ryujinx/PCSX2/Xenia)",
            Description = "模拟线程钉最优核心,高优先级,防篡改 + 独显 —— 模拟器最吃单核性能",
            Cores = "best", CpuPriority = "high", IoPriority = "high", JobEnforce = true,
            GpuPreference = Gpu.GpuPreference.HighPerformance
        },
        new()
        {
            Id = "mmo", Name = "🌍 MMO(魔兽/FF14/原神/星穹)",
            Description = "主线程优先核心 + 网络/IO 优先,群体场景减少卡顿 + 独显",
            Cores = "best", CpuPriority = "aboveNormal", IoPriority = "high",
            GpuPreference = Gpu.GpuPreference.HighPerformance
        },
        new()
        {
            Id = "strategy", Name = "♟ 策略/经营(文明/全战/戴森球)",
            Description = "全核吞吐优先(回合计算/大场面),高优先级 + 高IO + 独显",
            Cores = "all", CpuPriority = "aboveNormal", IoPriority = "high",
            GpuPreference = Gpu.GpuPreference.HighPerformance
        },
        new()
        {
            Id = "balanced", Name = "🔋 均衡(省电/低配机器)",
            Description = "核心分配为主,优先级适中 —— 兼顾性能与功耗",
            Cores = "best", CpuPriority = "aboveNormal", GpuPreference = Gpu.GpuPreference.Auto
        }
    };

    /// <summary>把预设的 "best" 核心解析为位掩码。</summary>
    public static ulong ResolveCoreMask(string cores, CpuTopology topo)
    {
        if (cores == "all")
        {
            ulong all = topo.PcoreMask | topo.EcoreMask;
            return all != 0 ? all : 1;
        }
        if (cores == "best")
        {
            // 所有品牌统一:第一个可用性能线程。
            // Intel 混合 = 第一个 P 核(含 SMT);AMD = 物理核心 0 的 SMT0,
            // 该线程在 AMD 上通常拥有最高 CPPC boost,是单核性能最优解。
            return 1UL << Math.Max(0, topo.BestCoreIndex);
        }
        string hex = cores.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        return ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out ulong m) ? m : 1;
    }
}
