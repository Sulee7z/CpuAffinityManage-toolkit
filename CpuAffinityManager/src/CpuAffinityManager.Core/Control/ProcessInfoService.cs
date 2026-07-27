using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CpuAffinityManager.ProcOps;

/// <summary>Extended per-process information (handles, threads, memory, network).</summary>
public sealed class ProcessSummary
{
    public int Pid { get; init; }
    public int Handles { get; init; }
    public int Threads { get; init; }
    public long WorkingSetBytes { get; init; }
    public long PrivateBytes { get; init; }
    public string IntegrityLevel { get; init; } = "";
    public List<string> Connections { get; init; } = new();
}

/// <summary>
/// Gathers extended process information: handle/thread counts, memory, and network
/// connections (parsed from <c>netstat -ano</c>, avoiding fragile iphlpapi marshaling).
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProcessInfoService
{
    public static ProcessSummary GetSummary(int pid)
    {
        int handles = 0, threads = 0;
        long ws = 0, pb = 0;
        try
        {
            using var p = Process.GetProcessById(pid);
            handles = p.HandleCount;
            threads = p.Threads.Count;
            ws = p.WorkingSet64;
            pb = p.PrivateMemorySize64;
        }
        catch { }

        return new ProcessSummary
        {
            Pid = pid,
            Handles = handles,
            Threads = threads,
            WorkingSetBytes = ws,
            PrivateBytes = pb,
            IntegrityLevel = GetIntegrityLevel(pid),
            Connections = GetConnections(pid)
        };
    }

    /// <summary>Returns the process integrity level: 系统 / 高 / 中 / 低 / 不可信 / 未知.</summary>
    public static string GetIntegrityLevel(int pid)
    {
        IntPtr hProc = OpenProcess(0x1000 /*QUERY_LIMITED_INFORMATION*/, false, (uint)pid);
        if (hProc == IntPtr.Zero) return "未知";
        IntPtr hTok = IntPtr.Zero;
        IntPtr buf = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(hProc, 0x0008 /*TOKEN_QUERY*/, out hTok)) return "未知";
            GetTokenInformation(hTok, 25 /*TokenIntegrityLevel*/, IntPtr.Zero, 0, out uint len);
            if (len == 0) return "未知";
            buf = Marshal.AllocHGlobal((int)len);
            if (!GetTokenInformation(hTok, 25, buf, len, out len)) return "未知";

            // TOKEN_MANDATORY_LABEL = SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes; }
            IntPtr pSid = Marshal.ReadIntPtr(buf);
            IntPtr pCount = GetSidSubAuthorityCount(pSid);
            int count = Marshal.ReadByte(pCount);
            IntPtr pRid = GetSidSubAuthority(pSid, (uint)(count - 1));
            uint rid = (uint)Marshal.ReadInt32(pRid);
            return rid switch
            {
                < 0x1000 => "不可信",
                < 0x2000 => "低",
                < 0x3000 => "中",
                < 0x4000 => "高",
                _        => "系统"
            };
        }
        catch { return "未知"; }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            if (hTok != IntPtr.Zero) CloseHandle(hTok);
            CloseHandle(hProc);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, uint len, out uint ret);
    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

    /// <summary>Returns this process's TCP/UDP connections as display strings.</summary>
    public static List<string> GetConnections(int pid)
    {
        var list = new List<string>();
        try
        {
            var psi = new ProcessStartInfo("netstat.exe", "-ano")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return list;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            string pidStr = pid.ToString();
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (!(line.StartsWith("TCP") || line.StartsWith("UDP"))) continue;

                var cols = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (cols.Length < 4) continue;
                if (cols[^1] != pidStr) continue;

                // TCP  local  remote  STATE  pid   |   UDP  local  *:*  pid
                string proto = cols[0];
                string local = cols[1];
                string remote = cols[2];
                string state = cols.Length >= 5 ? cols[3] : "";
                list.Add($"{proto} {local} → {remote} {state}".Trim());
                if (list.Count >= 100) break;
            }
        }
        catch { }
        return list;
    }
}
