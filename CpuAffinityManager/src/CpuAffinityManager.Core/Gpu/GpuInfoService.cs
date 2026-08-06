using System.Management;

namespace CpuAffinityManager.Gpu;

/// <summary>显卡枚举信息(WMI Win32_VideoController)。</summary>
public sealed class GpuInfo
{
    public string Name { get; init; } = "";
    public string DriverVersion { get; init; } = "";
    public string VideoMode { get; init; } = "";
    public ulong AdapterRamMb { get; init; }
    public int VideoProcessorCount { get; init; }

    /// <summary>是否为当前主显示适配器。</summary>
    public bool IsPrimary { get; init; }

    /// <summary>大显存(2GB+)独显视为游戏 GPU。</summary>
    public bool IsDedicatedGamingGpu => !string.IsNullOrEmpty(Name) && AdapterRamMb >= 2048;

    public string RamText => AdapterRamMb >= 1024
        ? $"{AdapterRamMb / 1024.0:0.#} GB"
        : $"{AdapterRamMb} MB";
}

/// <summary>进程级 GPU 偏好(等价于"把游戏绑到独显")。</summary>
public enum GpuPreference
{
    /// <summary>系统默认(通常自动选择)。</summary>
    Auto = 0,

    /// <summary>强制高性能 GPU(独显)—— 游戏最佳选择。</summary>
    HighPerformance = 2,

    /// <summary>节能 GPU(核显)—— 低功耗场景。</summary>
    PowerSaving = 1
}

/// <summary>
/// GPU 识别与按进程 GPU 偏好服务。Windows 没有 CPU 式 GPU 亲和性,
/// 但 DirectX 提供按进程 GPU 偏好(UserGpuPreferences 注册表),等效于
/// "游戏跑独显、后台跑核显",是游戏性能优化的关键 GPU 手段。
/// </summary>
public static class GpuInfoService
{
    /// <summary>枚举系统中的显卡。</summary>
    public static List<GpuInfo> Enumerate()
    {
        var result = new List<GpuInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name,AdapterRAM,DriverVersion,VideoModeDescription,CurrentBitsPerPixel,CurrentHorizontalResolution,CurrentVerticalResolution,VideoProcessor,VideoArchitecture,Availability FROM Win32_VideoController");
            foreach (var obj in searcher.Get())
            {
                ulong ramBytes = obj["AdapterRAM"] is UInt32 u32 ? u32 : obj["AdapterRAM"] is UInt64 u64 ? u64 : 0;
                bool isPrimary = (obj["CurrentHorizontalResolution"] as UInt32? ?? 0) > 0;

                result.Add(new GpuInfo
                {
                    Name = obj["Name"]?.ToString()?.Trim() ?? "",
                    DriverVersion = obj["DriverVersion"]?.ToString()?.Trim() ?? "",
                    VideoMode = $"{obj["CurrentHorizontalResolution"]?.ToString()?.Trim()}×{obj["CurrentVerticalResolution"]?.ToString()?.Trim()} {obj["CurrentBitsPerPixel"]?.ToString()?.Trim()}bit",
                    AdapterRamMb = ramBytes / (1024 * 1024),
                    VideoProcessorCount = (int)(obj["VideoProcessor"] as UInt32? ?? 0),
                    IsPrimary = isPrimary
                });
            }
        }
        catch
        {
            // WMI 不可用时静默降级
        }
        return result;
    }

    /// <summary>读取某个进程(按 exe 名)的 GPU 偏好。</summary>
    public static GpuPreference GetProcessGpuPreference(string exeName)
    {
        try
        {
            string key = exeName + "|";
            object? raw = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\DirectX\UserGpuPreferences",
                key, null);
            string? value = raw as string;
            if (string.IsNullOrWhiteSpace(value)) return GpuPreference.Auto;
            if (value.Contains("GpuPreference=2", StringComparison.OrdinalIgnoreCase)) return GpuPreference.HighPerformance;
            if (value.Contains("GpuPreference=1", StringComparison.OrdinalIgnoreCase)) return GpuPreference.PowerSaving;
            return GpuPreference.Auto;
        }
        catch { return GpuPreference.Auto; }
    }

    /// <summary>
    /// 设置进程 GPU 偏好(UserGpuPreferences)。游戏进程设为高性能(独显),
    /// 后台进程可设为节能(核显),实现"游戏绑独显"。
    /// </summary>
    public static void SetProcessGpuPreference(string exeName, GpuPreference preference)
    {
        try
        {
            const string hive = @"HKEY_CURRENT_USER\Software\Microsoft\DirectX\UserGpuPreferences";
            string key = exeName + "|";
            if (preference == GpuPreference.Auto)
            {
                using var subKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\DirectX\UserGpuPreferences", writable: true);
                subKey?.DeleteValue(key, throwOnMissingValue: false);
                return;
            }
            string existing = Microsoft.Win32.Registry.GetValue(hive, key, "") as string ?? "";
            string gpu = preference == GpuPreference.HighPerformance ? "GpuPreference=2" : "GpuPreference=1";
            string merged = existing;
            foreach (string part in existing.Split(';'))
            {
                if (part.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase))
                    merged = merged.Replace(part, gpu, StringComparison.OrdinalIgnoreCase);
            }
            if (!merged.Contains(gpu, StringComparison.OrdinalIgnoreCase))
                merged = merged.Length == 0 ? gpu + ";" : merged.TrimEnd(';') + ";" + gpu + ";";
            Microsoft.Win32.Registry.SetValue(hive, key, merged, Microsoft.Win32.RegistryValueKind.String);
        }
        catch
        {
            // 注册表写入失败静默(仅影响 GPU 偏好)
        }
    }

    /// <summary>清除进程 GPU 偏好(恢复系统默认)。</summary>
    public static void ClearProcessGpuPreference(string exeName)
        => SetProcessGpuPreference(exeName, GpuPreference.Auto);
}
