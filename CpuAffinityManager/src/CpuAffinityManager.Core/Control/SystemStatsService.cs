using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CpuAffinityManager.ProcOps;

/// <summary>
/// Lightweight overall system statistics: CPU utilization (delta-based) and
/// physical memory usage, both via native calls (no WMI / no PerformanceCounters,
/// keeping the app AOT-safe and dependency-free). Used by the dashboard.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SystemStatsService
{
    private static long _prevIdle;
    private static long _prevKernel;
    private static long _prevUser;
    private static bool _havePrev;

    /// <summary>
    /// Samples overall CPU usage (%) and physical memory usage (MB).
    /// CPU % is the delta since the previous call — the first call returns 0
    /// (there is no baseline to compare against yet).
    /// </summary>
    public static (double CpuPercent, ulong TotalMb, ulong UsedMb) Sample()
    {
        ulong totalMb = 0, usedMb = 0;
        try
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref m))
            {
                totalMb = m.ullTotalPhys / (1024 * 1024);
                usedMb = (m.ullTotalPhys - m.ullAvailPhys) / (1024 * 1024);
            }
        }
        catch { }

        double cpu = 0;
        try
        {
            if (GetSystemTimes(out long idle, out long kernel, out long user))
            {
                if (_havePrev)
                {
                    long totalDelta = (kernel - _prevKernel) + (user - _prevUser);
                    long idleDelta = idle - _prevIdle;
                    if (totalDelta > 0)
                    {
                        cpu = (totalDelta - idleDelta) * 100.0 / totalDelta;
                        if (cpu < 0) cpu = 0;
                        if (cpu > 100) cpu = 100;
                    }
                }
                _prevIdle = idle;
                _prevKernel = kernel;
                _prevUser = user;
                _havePrev = true;
            }
        }
        catch { }

        return (cpu, totalMb, usedMb);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);
}
