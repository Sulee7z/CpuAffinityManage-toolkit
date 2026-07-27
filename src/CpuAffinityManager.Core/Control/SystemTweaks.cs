using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Serilog;

namespace CpuAffinityManager.ProcOps;

/// <summary>Known Windows power schemes.</summary>
public enum PowerPlan { Balanced, HighPerformance, PowerSaver, UltimatePerformance }

/// <summary>
/// System-wide tweaks: memory cleanup, timer resolution, power plan switching, and
/// foreground priority separation. Each mutating call reads the value back afterwards
/// and returns a human-readable result so the UI can show what actually happened.
/// Several of these are global and require admin.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SystemTweaks
{
    /// <summary>
    /// Deletes leftover duplicate "Ultimate Performance" power plans, keeping only our
    /// fixed one and never touching the currently-active plan. Returns a summary message.
    /// </summary>
    public static string CleanDuplicatePowerPlans()
    {
        try
        {
            string list = RunOut("powercfg.exe", "/list");
            int deleted = 0;
            foreach (var line in list.Split('\n'))
            {
                if (line.Contains('*')) continue; // active scheme — cannot delete
                var gm = Regex.Match(line, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
                if (!gm.Success) continue;
                string g = gm.Value;
                if (g.Equals(UltimateFixedGuid, StringComparison.OrdinalIgnoreCase)) continue; // keep ours

                var nm = Regex.Match(line, @"\(([^)]+)\)");
                string name = nm.Success ? nm.Groups[1].Value : "";
                bool isUltimate = g.Equals(UltimateSrcGuid, StringComparison.OrdinalIgnoreCase)
                    || name.Contains("卓越")
                    || name.IndexOf("Ultimate", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isUltimate)
                {
                    RunOut("powercfg.exe", $"-delete {g}");
                    deleted++;
                }
            }
            return deleted > 0 ? $"已清理 {deleted} 个多余的『卓越性能』计划" : "没有发现多余的电源计划";
        }
        catch (Exception ex) { Log.Warning(ex, "CleanDuplicatePowerPlans failed"); return "清理失败:" + ex.Message; }
    }

    /// <summary>Flushes the Windows system file cache (needs admin). Returns a message.</summary>
    public static string ClearSystemFileCache()
    {
        try
        {
            bool ok = SetSystemFileCacheSize((IntPtr)(-1), (IntPtr)(-1), 0);
            return ok ? "已清理系统文件缓存" : "清理失败:请以管理员身份运行";
        }
        catch (Exception ex) { Log.Warning(ex, "ClearSystemFileCache failed"); return "清理失败:" + ex.Message; }
    }

    /// <summary>Trims every accessible process's working set. Returns count trimmed.</summary>
    public static int CleanSystemMemory()
    {
        int n = 0;
        foreach (var p in Process.GetProcesses())
        {
            try { if (p.Id > 4 && ProcessControlService.EmptyWorkingSet(p.Id)) n++; }
            catch { }
            finally { try { p.Dispose(); } catch { } }
        }
        Log.Information("System memory cleanup trimmed {N} processes", n);
        return n;
    }

    // ── Timer resolution ──

    /// <summary>
    /// Sets the global timer resolution (ms). Returns a message with the ACTUAL
    /// resolution read back from the kernel, so failures are obvious.
    /// </summary>
    public static string SetTimerResolution(double milliseconds)
    {
        try
        {
            if (milliseconds >= 15.0)
            {
                NtSetTimerResolution(0, false, out _);
                try { timeEndPeriod(1); } catch { }
                double cur0 = QueryTimerMs();
                return $"已恢复默认,当前计时器分辨率 {cur0:0.00} ms";
            }

            uint desired = (uint)Math.Clamp(milliseconds * 10000.0, 5000, 156250); // 100-ns units
            int st = NtSetTimerResolution(desired, true, out _);
            if (milliseconds <= 1.0) { try { timeBeginPeriod(1); } catch { } }

            double cur = QueryTimerMs();
            if (st == 0 && cur <= milliseconds + 0.2)
                return $"计时器分辨率已设为 {cur:0.00} ms(内核确认)";
            return $"已请求 {milliseconds:0.00} ms,但当前实际为 {cur:0.00} ms(status=0x{st:X})";
        }
        catch (Exception ex) { Log.Warning(ex, "SetTimerResolution failed"); return "设置计时器分辨率失败:" + ex.Message; }
    }

    private static double QueryTimerMs()
    {
        try { if (NtQueryTimerResolution(out _, out _, out uint cur) == 0) return cur / 10000.0; }
        catch { }
        return -1;
    }

    // ── Power plan ──

    public static string SetPowerPlan(PowerPlan plan)
    {
        try
        {
            string guid = plan switch
            {
                PowerPlan.HighPerformance     => "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
                PowerPlan.PowerSaver          => "a1841308-3541-4fab-bc81-f71556f20b4a",
                PowerPlan.UltimatePerformance => "e9a42b02-d5df-448d-aa00-03f14749eb61",
                _                             => "381b4222-f694-41f0-9685-ff5bb260df2e" // Balanced
            };

            // "Ultimate Performance" is hidden and each plain duplicate gets a NEW random
            // GUID, so previous "reuse-by-name" detection failed on non-English Windows
            // (powercfg prints its localized name in the OEM codepage). Fix: duplicate the
            // template into a FIXED destination GUID of our own — GUID matching is ASCII,
            // so it is idempotent regardless of locale/encoding, and never piles up copies.
            if (plan == PowerPlan.UltimatePerformance)
            {
                string list = RunOut("powercfg.exe", "/list");
                if (list.IndexOf(UltimateFixedGuid, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    RunOut("powercfg.exe", $"-duplicatescheme {UltimateSrcGuid} {UltimateFixedGuid}");
                    RunOut("powercfg.exe", $"-changename {UltimateFixedGuid} \"卓越性能\" \"由 CPU 亲和性管理器启用\"");
                }
                guid = UltimateFixedGuid;
            }

            RunOut("powercfg.exe", $"/setactive {guid}");

            // Read back the active scheme to confirm.
            string active = RunOut("powercfg.exe", "/getactivescheme");
            string name = ExtractSchemeName(active);
            if (active.IndexOf(guid, StringComparison.OrdinalIgnoreCase) >= 0)
                return $"已切换电源模式,当前:{name}";
            return plan == PowerPlan.UltimatePerformance
                ? "切换失败:本机 Windows 版本可能不支持『卓越性能』"
                : $"切换失败,当前仍为:{name}";
        }
        catch (Exception ex) { Log.Warning(ex, "SetPowerPlan failed"); return "切换电源模式失败:" + ex.Message; }
    }

    // Windows' hidden Ultimate Performance template, and our own fixed destination GUID
    // so enabling it is idempotent (never creates more than one copy).
    private const string UltimateSrcGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    private const string UltimateFixedGuid = "e9a42b02-d5df-448d-aa00-03f14749ebff";

    private static string ExtractSchemeName(string getActiveOutput)
    {
        // e.g. "电源方案 GUID: 8c5e... (高性能)"  /  "Power Scheme GUID: ... (High performance)"
        var m = Regex.Match(getActiveOutput, @"\(([^)]+)\)");
        return m.Success ? m.Groups[1].Value.Trim() : getActiveOutput.Trim();
    }

    // ── Foreground priority separation ──

    public static string SetPrioritySeparation(int value)
    {
        try
        {
            string key = @"HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl";
            RunOut("reg.exe", $"add \"{key}\" /v Win32PrioritySeparation /t REG_DWORD /d {value} /f");

            // Read back to prove it stuck (fails silently without admin).
            string q = RunOut("reg.exe", $"query \"{key}\" /v Win32PrioritySeparation");
            var m = Regex.Match(q, @"Win32PrioritySeparation\s+REG_DWORD\s+0x([0-9a-fA-F]+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int got))
            {
                if (got == value)
                    return $"已写入 Win32PrioritySeparation = {value}(读回确认;部分场景需重启完全生效)";
                return $"写入的值为 {got},与预期 {value} 不一致";
            }
            return "写入失败:请以管理员身份运行(HKLM 需要管理员权限)";
        }
        catch (Exception ex) { Log.Warning(ex, "SetPrioritySeparation failed"); return "设置失败:" + ex.Message; }
    }

    // ── Sleep / display control ──

    /// <summary>Keeps the system (and optionally display) awake, or restores normal behavior.</summary>
    public static string SetSleepPrevention(bool prevent)
    {
        try
        {
            EXECUTION_STATE flags = prevent
                ? EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED | EXECUTION_STATE.ES_DISPLAY_REQUIRED
                : EXECUTION_STATE.ES_CONTINUOUS;
            var prev = SetThreadExecutionState(flags);
            if (prev == 0) return "设置失败";
            return prevent ? "已防止系统睡眠与息屏(保持唤醒)" : "已恢复正常睡眠策略";
        }
        catch (Exception ex) { Log.Warning(ex, "SetSleepPrevention failed"); return "设置失败:" + ex.Message; }
    }

    /// <summary>Puts the system to sleep immediately.</summary>
    public static string SleepNow()
    {
        try { return SetSuspendState(false, false, false) ? "正在进入睡眠…" : "无法进入睡眠"; }
        catch (Exception ex) { Log.Warning(ex, "SleepNow failed"); return "无法进入睡眠:" + ex.Message; }
    }

    // ── Display brightness (WMI; mainly laptops / DDC-capable monitors) ──

    public static string SetBrightness(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            int applied = 0;
            foreach (ManagementObject mo in searcher.Get())
            {
                try { mo.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)percent }); applied++; }
                catch { }
                finally { mo.Dispose(); }
            }
            return applied > 0 ? $"已将亮度设为 {percent}%" : "本机不支持通过 WMI 调节亮度(多见于台式外接显示器)";
        }
        catch (Exception ex) { Log.Warning(ex, "SetBrightness failed"); return "调节亮度失败:" + ex.Message; }
    }

    // ── Network DNS ──

    public static string SetDns(string preset)
    {
        try
        {
            (string primary, string secondary)? dns = preset switch
            {
                "ali"        => ("223.5.5.5", "223.6.6.6"),
                "tencent"    => ("119.29.29.29", "182.254.116.116"),
                "cloudflare" => ("1.1.1.1", "1.0.0.1"),
                "google"     => ("8.8.8.8", "8.8.4.4"),
                _            => null // auto / dhcp
            };

            var ifaces = GetConnectedInterfaces();
            if (ifaces.Count == 0) return "未找到已连接的网络适配器";

            foreach (var name in ifaces)
            {
                if (dns == null)
                {
                    RunOut("netsh.exe", $"interface ip set dns name=\"{name}\" dhcp");
                }
                else
                {
                    RunOut("netsh.exe", $"interface ip set dns name=\"{name}\" static {dns.Value.primary} primary");
                    RunOut("netsh.exe", $"interface ip add dns name=\"{name}\" {dns.Value.secondary} index=2");
                }
            }

            string desc = dns == null ? "自动获取 (DHCP)" : $"{dns.Value.primary} / {dns.Value.secondary}";
            return $"已对 {ifaces.Count} 个适配器设置 DNS:{desc}";
        }
        catch (Exception ex) { Log.Warning(ex, "SetDns failed"); return "设置 DNS 失败:" + ex.Message; }
    }

    public static string SetMtu(int mtu)
    {
        try
        {
            mtu = Math.Clamp(mtu, 576, 9000);
            var ifaces = GetConnectedInterfaces();
            if (ifaces.Count == 0) return "未找到已连接的网络适配器";
            foreach (var name in ifaces)
                RunOut("netsh.exe", $"interface ipv4 set subinterface \"{name}\" mtu={mtu} store=persistent");
            return $"已对 {ifaces.Count} 个适配器设置 MTU = {mtu}";
        }
        catch (Exception ex) { Log.Warning(ex, "SetMtu failed"); return "设置 MTU 失败:" + ex.Message; }
    }

    // ── Standby memory list ──

    /// <summary>
    /// Purges the system standby (cached) memory list, freeing RAM held as file cache.
    /// Needs admin + SeProfileSingleProcessPrivilege.
    /// </summary>
    public static string CleanStandbyList()
    {
        try
        {
            Native.TokenPrivileges.Enable("SeProfileSingleProcessPrivilege");
            int command = 4; // MemoryPurgeStandbyList
            int status = NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
            return status == 0
                ? "已清理待机(standby)内存列表"
                : $"清理失败(status=0x{status:X8},需以管理员运行)";
        }
        catch (Exception ex) { Log.Warning(ex, "CleanStandbyList failed"); return "清理失败:" + ex.Message; }
    }

    private const int SystemMemoryListInformation = 80;

    private static List<string> GetConnectedInterfaces()
    {
        var result = new List<string>();
        string outp = RunOut("netsh.exe", "interface show interface");
        foreach (var line in outp.Split('\n'))
        {
            var parts = Regex.Split(line.Trim(), @"\s{2,}");
            if (parts.Length < 4) continue;
            // columns: 管理员状态 状态 类型 接口名称
            string state = parts[1].Trim();
            if (state == "已连接" || state.Equals("Connected", StringComparison.OrdinalIgnoreCase))
                result.Add(parts[^1].Trim());
        }
        return result;
    }

    // ── DWM / MMCSS multimedia scheduling ──

    /// <summary>
    /// Tunes MMCSS (Multimedia Class Scheduler) — SystemResponsiveness and the Games
    /// task profile (GPU/CPU priority, scheduling category) — for smoother games/media,
    /// or restores Windows defaults.
    /// </summary>
    public static string ApplyMmcss(bool optimize)
    {
        try
        {
            string sp = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
            string games = sp + @"\Tasks\Games";
            if (optimize)
            {
                RunOut("reg.exe", $"add \"{sp}\" /v SystemResponsiveness /t REG_DWORD /d 0 /f");
                RunOut("reg.exe", $"add \"{games}\" /v \"GPU Priority\" /t REG_DWORD /d 8 /f");
                RunOut("reg.exe", $"add \"{games}\" /v Priority /t REG_DWORD /d 6 /f");
                RunOut("reg.exe", $"add \"{games}\" /v \"Scheduling Category\" /t REG_SZ /d High /f");
                RunOut("reg.exe", $"add \"{games}\" /v \"SFIO Priority\" /t REG_SZ /d High /f");
                return "已应用多媒体/游戏调度优化(MMCSS,重启后完全生效)";
            }
            RunOut("reg.exe", $"add \"{sp}\" /v SystemResponsiveness /t REG_DWORD /d 20 /f");
            RunOut("reg.exe", $"add \"{games}\" /v \"GPU Priority\" /t REG_DWORD /d 8 /f");
            RunOut("reg.exe", $"add \"{games}\" /v Priority /t REG_DWORD /d 2 /f");
            RunOut("reg.exe", $"add \"{games}\" /v \"Scheduling Category\" /t REG_SZ /d Medium /f");
            RunOut("reg.exe", $"add \"{games}\" /v \"SFIO Priority\" /t REG_SZ /d Normal /f");
            return "已恢复默认多媒体调度";
        }
        catch (Exception ex) { Log.Warning(ex, "ApplyMmcss failed"); return "设置失败:" + ex.Message; }
    }

    /// <summary>
    /// One-click graphics/performance preset: hardware-accelerated GPU scheduling +
    /// MMCSS gaming profile + high-performance power plan.
    /// </summary>
    public static string ApplyGraphicsPreset()
    {
        try
        {
            RunOut("reg.exe", "add \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers\" /v HwSchMode /t REG_DWORD /d 2 /f");
            ApplyMmcss(true);
            SetPowerPlan(PowerPlan.HighPerformance);
            return "已应用图形性能优化(GPU 硬件调度 + 多媒体优先 + 高性能电源;GPU 调度需重启生效)";
        }
        catch (Exception ex) { Log.Warning(ex, "ApplyGraphicsPreset failed"); return "设置失败:" + ex.Message; }
    }

    // ── DPC 延迟 / 响应速度优化(系统级) ──

    /// <summary>
    /// System-wide low-latency / high-responsiveness bundle, meant to cut DPC/ISR latency
    /// and input lag. Toggling <paramref name="on"/> off restores Windows defaults.
    /// It combines four safe, well-known tweaks:
    ///   1. Un-park all CPU cores (SUB_PROCESSOR / CPMINCORES = 100%) so no core sleeps —
    ///      parked cores are a common source of DPC latency spikes.
    ///   2. Disable Power Throttling (EcoQoS) system-wide so background work isn't clamped.
    ///   3. MMCSS SystemResponsiveness = 0 (max foreground responsiveness).
    ///   4. Switch to the High-Performance power plan.
    /// True per-device interrupt affinity (steering a NIC/GPU IRQ to specific cores) is
    /// hardware-specific and risky, so it is intentionally NOT touched here.
    /// </summary>
    public static string ApplyLatencyOptimization(bool on)
    {
        try
        {
            // Processor power subgroup + "Processor performance core parking min cores".
            const string SUB_PROCESSOR = "54533251-82be-4824-96c1-47b60b740d00";
            const string CPMINCORES    = "0cc5b647-c1df-4637-891a-dec35c318583";
            const string throttleKey   = @"HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling";
            string sp = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";

            if (on)
            {
                // 1) Un-park every core (min cores 100%) on both AC and DC.
                RunOut("powercfg.exe", $"-setacvalueindex SCHEME_CURRENT {SUB_PROCESSOR} {CPMINCORES} 100");
                RunOut("powercfg.exe", $"-setdcvalueindex SCHEME_CURRENT {SUB_PROCESSOR} {CPMINCORES} 100");
                RunOut("powercfg.exe", "-setactive SCHEME_CURRENT");
                // 2) Disable Power Throttling globally.
                RunOut("reg.exe", $"add \"{throttleKey}\" /v PowerThrottlingOff /t REG_DWORD /d 1 /f");
                // 3) Max MMCSS responsiveness.
                RunOut("reg.exe", $"add \"{sp}\" /v SystemResponsiveness /t REG_DWORD /d 0 /f");
                // 4) High-performance power plan.
                SetPowerPlan(PowerPlan.HighPerformance);
                return "已应用低延迟/高响应优化(核心全解泊 + 关闭电源限流 + 多媒体最高响应 + 高性能电源;部分项重启后完全生效)";
            }

            // Restore defaults.
            RunOut("powercfg.exe", $"-setacvalueindex SCHEME_CURRENT {SUB_PROCESSOR} {CPMINCORES} 10");
            RunOut("powercfg.exe", $"-setdcvalueindex SCHEME_CURRENT {SUB_PROCESSOR} {CPMINCORES} 10");
            RunOut("powercfg.exe", "-setactive SCHEME_CURRENT");
            RunOut("reg.exe", $"add \"{throttleKey}\" /v PowerThrottlingOff /t REG_DWORD /d 0 /f");
            RunOut("reg.exe", $"add \"{sp}\" /v SystemResponsiveness /t REG_DWORD /d 20 /f");
            SetPowerPlan(PowerPlan.Balanced);
            return "已恢复默认延迟/响应设置(核心泊车、电源限流、多媒体响应均复位)";
        }
        catch (Exception ex) { Log.Warning(ex, "ApplyLatencyOptimization failed"); return "设置失败:" + ex.Message; }
    }

    // ── helpers ──

    private static string RunOut(string file, string args)
    {
        try
        {
            // Do NOT force UTF-8: powercfg/reg print in the console OEM codepage (e.g. GBK
            // on Chinese Windows). Letting .NET use the default console encoding keeps
            // localized scheme names readable. GUID/hex parsing is ASCII either way.
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            string outp = p.StandardOutput.ReadToEnd();
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit(6000);
            return outp + "\n" + err;
        }
        catch { return ""; }
    }

    // BOOLEAN is 1 byte — must be marshaled as U1, not the default 4-byte Win32 BOOL.
    [DllImport("ntdll.dll")]
    private static extern int NtSetTimerResolution(uint DesiredResolution,
        [MarshalAs(UnmanagedType.U1)] bool SetResolution, out uint CurrentResolution);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(out uint MinimumResolution, out uint MaximumResolution, out uint CurrentResolution);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint timeEndPeriod(uint uPeriod);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetSystemFileCacheSize(IntPtr MinimumFileCacheSize, IntPtr MaximumFileCacheSize, uint Flags);

    [Flags]
    private enum EXECUTION_STATE : uint
    {
        ES_CONTINUOUS = 0x80000000,
        ES_SYSTEM_REQUIRED = 0x00000001,
        ES_DISPLAY_REQUIRED = 0x00000002
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.Bool)] bool hibernate,
        [MarshalAs(UnmanagedType.Bool)] bool forceCritical,
        [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int SystemInformationClass, ref int SystemInformation, int SystemInformationLength);
}
