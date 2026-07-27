using System.Diagnostics;
using System.Runtime.InteropServices;
using CpuAffinityManager.Cpu;
using CpuAffinityManager.Engine;
using CpuAffinityManager.Native;

namespace CpuAffinityManager.Enforcement;

/// <summary>
/// Central enforcement service — applies CPU affinity rules using the appropriate
/// mechanism: soft CPU Sets, hard process affinity, or Job Object enforcement.
/// </summary>
public class EnforcementService : IEnforcementService
{
    private readonly IRuleEngine _ruleEngine;
    private readonly ICpuTopologyService _topoService;
    private readonly JobObjectManager _jobManager;

    public EnforcementService(IRuleEngine ruleEngine, ICpuTopologyService topoService)
    {
        _ruleEngine = ruleEngine;
        _topoService = topoService;
        _jobManager = new JobObjectManager();
    }

    /// <summary>
    /// Applies a rule to a process by PID. Dispatches to the appropriate
    /// enforcement mechanism based on the rule's level.
    /// </summary>
    public bool Apply(int pid, RuleEntry rule, CpuTopology topology)
    {
        if (pid <= 0 || rule?.Action == null)
            return false;

        // Build the affinity mask with composite mode + socket support
        string mode = rule.Action.Mode;

        // Append socket suffix if a specific socket is requested
        if (rule.Action.SocketIndex.HasValue && rule.Action.SocketIndex.Value >= 0)
        {
            mode += $"@socket{rule.Action.SocketIndex.Value}";
        }

        ulong mask = CpuTopology.BuildMask(topology, mode, rule.Action.GetCustomMask());

        if (mask == 0)
            return false;

        bool applied = rule.Action.Level switch
        {
            "soft-cpu-sets" => ApplyCpuSets(pid, mask),
            "hard-affinity" => ApplyHardAffinity(pid, mask),
            "job-enforced" => ApplyJobEnforced(pid, mask, lockBreakaway: false) || ApplyHardAffinity(pid, mask),
            _ => false
        };

        // Apply optional CPU priority class hint (e.g. "high", "belowNormal")
        if (applied && !string.IsNullOrWhiteSpace(rule.Action.CpuPriority))
            ApplyPriorityClass(pid, rule.Action.CpuPriority);

        // Apply optional professional extras (IO/memory priority, efficiency mode).
        if (applied)
        {
            if (!string.IsNullOrWhiteSpace(rule.Action.IoPriority))
                ProcOps.ProcessControlService.SetIoPriority(pid, IoLevel(rule.Action.IoPriority));
            if (rule.Action.MemoryPriority is int mp)
                ProcOps.ProcessControlService.SetMemoryPriority(pid, mp);
            if (rule.Action.EfficiencyMode)
                ProcOps.ProcessControlService.SetEfficiencyMode(pid, true);
            // 全核可用但优先某些核心:仅设线程理想处理器,不缩小亲和性。
            ulong prefer = rule.Action.GetPreferredMask();
            if (prefer != 0)
                ProcOps.ProcessControlService.ApplyPreferredCores(pid, prefer);
        }

        return applied;
    }

    private static int IoLevel(string s) => s.Trim().ToLowerInvariant() switch
    {
        "verylow" or "idle" => 0,
        "low" => 1,
        "high" => 3,
        _ => 2 // normal
    };

    /// <summary>
    /// Sets the process priority class from a rule's cpuPriority hint.
    /// Accepted values: low, belowNormal, normal, aboveNormal, high, realtime.
    /// Failures are non-fatal — the affinity has already been applied.
    /// </summary>
    private static void ApplyPriorityClass(int pid, string priority)
    {
        try
        {
            ProcessPriorityClass? cls = priority.Trim().ToLowerInvariant() switch
            {
                "low" or "idle" => ProcessPriorityClass.Idle,
                "belownormal" => ProcessPriorityClass.BelowNormal,
                "normal" => ProcessPriorityClass.Normal,
                "abovenormal" => ProcessPriorityClass.AboveNormal,
                "high" => ProcessPriorityClass.High,
                "realtime" => ProcessPriorityClass.RealTime,
                _ => null
            };

            if (cls == null)
                return;

            using var proc = Process.GetProcessById(pid);
            proc.PriorityClass = cls.Value;
        }
        catch
        {
            // Access denied / process exited — priority is best-effort only.
        }
    }

    /// <summary>
    /// Releases all Job Object affinity limits created during this session and
    /// disposes them. Invoked on shutdown to avoid leaving lingering restrictions.
    /// </summary>
    public void ShutdownCleanup()
    {
        try { _jobManager.ReleaseAllLimitsAndDispose(); } catch { }
    }

    public bool Relax(int pid, CpuTopology topology)
    {
        if (pid <= 0)
            return false;

        ulong mask = CpuTopology.BuildMask(topology, "all-cores");
        if (mask == 0)
            return false;

        bool jobUpdated = ApplyJobEnforced(pid, mask, lockBreakaway: false);
        bool hardUpdated = ApplyHardAffinity(pid, mask);
        return jobUpdated || hardUpdated;
    }

    /// <summary>
    /// Applies CPU Sets as a soft preference (lowest priority mechanism).
    /// Uses SetProcessDefaultCpuSets with REAL CPU-set IDs — the previous
    /// NtSetInformationProcess(0x42, rawMask) call passed the wrong argument
    /// type (an affinity mask instead of an ID array) and silently did nothing.
    /// </summary>
    private static bool ApplyCpuSets(int pid, ulong mask)
        => ProcOps.ProcessControlService.SetDefaultCpuSets(pid, mask);

    /// <summary>
    /// Applies hard process affinity (middle priority).
    /// Uses NtSetInformationProcess(ProcessAffinityMask = 0x15).
    /// </summary>
    private static unsafe bool ApplyHardAffinity(int pid, ulong mask)
    {
        IntPtr hProcess = OpenProcessForWrite(pid);
        if (hProcess == IntPtr.Zero)
            return false;

        try
        {
            UIntPtr maskPtr = (UIntPtr)mask;
            int status = NtdllImports.NtSetInformationProcess(
                hProcess,
                PROCESS_INFORMATION_CLASS.ProcessAffinityMask,
                ref maskPtr,
                (uint)sizeof(UIntPtr));

            if (status == 0)
                return true;

            return Kernel32Imports.SetProcessAffinityMask(hProcess, maskPtr);
        }
        finally
        {
            NtdllImports.NtClose(hProcess);
        }
    }

    /// <summary>
    /// Applies Job Object-based CPU affinity limit (highest priority, durable).
    /// </summary>
    public bool ApplyJobEnforced(int pid, ulong mask)
    {
        return ApplyJobEnforced(pid, mask, lockBreakaway: false);
    }

    /// <summary>
    /// Applies Job Object-based CPU affinity limit (highest priority, durable).
    /// This is the only mechanism that prevents the target process from
    /// modifying its own affinity.
    /// </summary>
    public bool ApplyJobEnforced(int pid, ulong mask, bool lockBreakaway)
    {
        // 1. Enable SeDebugPrivilege (needed for protected processes)
        TokenPrivileges.EnableDebugPrivilege();

        // 2. Open the target process — try NtOpenProcess first (bypass ACL),
        //    fall back to kernel32 OpenProcess
        IntPtr hProcess = OpenProcessForJob(pid);
        if (hProcess == IntPtr.Zero)
            return false;

        try
        {
            // 3. Create or get the Job Object for this PID
            IntPtr hJob = _jobManager.GetOrCreateJob(pid);
            if (hJob == IntPtr.Zero)
                return false;

            // 4. Set the CPU affinity limit on the Job Object
            if (!_jobManager.SetCpuAffinityLimit(hJob, mask))
                return false;

            // 5. If locking, prevent breakaway (child processes stay in Job)
            if (lockBreakaway)
            {
                _jobManager.PreventBreakaway(hJob);
            }

            // 6. Assign the process to the Job
            if (!_jobManager.AssignProcess(hJob, hProcess))
            {
                // Process might already be in a Job — check error
                int err = Marshal.GetLastWin32Error();
                // ERROR_ACCESS_DENIED (5) = already in a Job that disallows assignment
                // For now, fail silently. Future: try to nest job via NtSetInformationProcess
                if (err == 5)
                    return false;
                return false;
            }

            return true;
        }
        finally
        {
            NtdllImports.NtClose(hProcess);
        }
    }

    /// <summary>
    /// Scans all running processes and applies matching rules.
    /// </summary>
    public int ScanAndEnforce()
    {
        int affected = 0;
        var topology = _topoService.Detect();

        // Only resolve paths when some enabled rule actually needs them.
        bool needPath = false;
        foreach (var r in _ruleEngine.Rules)
        {
            if (r.Enabled && !string.IsNullOrEmpty(r.Match.Path)) { needPath = true; break; }
        }

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                int pid = process.Id;
                if (pid is 0 or 4)
                    continue;

                string processName = process.ProcessName + ".exe";

                // Fast native path query instead of the slow, exception-prone
                // Process.MainModule. Processes whose path can't be read still
                // participate in name-only rule matching (empty path).
                string fullPath = needPath ? (GetProcessPath(pid) ?? string.Empty) : string.Empty;

                var rule = _ruleEngine.Match(processName, fullPath);
                if (rule != null && Apply(pid, rule, topology))
                    affected++;
            }
            catch
            {
                // Process may have exited during scan
            }
            finally
            {
                try { process.Dispose(); } catch { }
            }
        }

        return affected;
    }

    /// <summary>
    /// Opens a process with sufficient rights for NtSetInformationProcess calls.
    /// Tries standard OpenProcess first, falls back to NtOpenProcess for protected processes.
    /// </summary>
    private static IntPtr OpenProcessForWrite(int pid)
    {
        // Try standard OpenProcess first (works for most processes)
        IntPtr hProcess = Kernel32Imports.OpenProcess(
            ProcessAccess.PROCESS_SET_INFORMATION | ProcessAccess.PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            (uint)pid);

        if (hProcess != IntPtr.Zero)
            return hProcess;

        // Fall back to NtOpenProcess (bypasses ACL with SeDebugPrivilege)
        return NtOpenProcessInternal(pid,
            ProcessAccess.PROCESS_SET_INFORMATION | ProcessAccess.PROCESS_QUERY_LIMITED_INFORMATION);
    }

    /// <summary>
    /// Opens a process for Job Object assignment — needs wider access rights.
    /// </summary>
    private static IntPtr OpenProcessForJob(int pid)
    {
        // AssignProcessToJobObject requires PROCESS_SET_QUOTA and PROCESS_TERMINATE.
        // Try standard first
        IntPtr hProcess = Kernel32Imports.OpenProcess(
            ProcessAccess.PROCESS_TERMINATE |
            ProcessAccess.PROCESS_SET_QUOTA |
            ProcessAccess.PROCESS_SET_INFORMATION |
            ProcessAccess.PROCESS_QUERY_INFORMATION |
            ProcessAccess.PROCESS_QUERY_LIMITED_INFORMATION |
            ProcessAccess.PROCESS_SUSPEND_RESUME |  // May be needed for some operations
            ProcessAccess.PROCESS_VM_READ,
            false,
            (uint)pid);

        if (hProcess != IntPtr.Zero)
            return hProcess;

        // Fall back to NtOpenProcess
        return NtOpenProcessInternal(pid,
            ProcessAccess.PROCESS_TERMINATE |
            ProcessAccess.PROCESS_SET_QUOTA |
            ProcessAccess.PROCESS_SET_INFORMATION |
            ProcessAccess.PROCESS_QUERY_INFORMATION |
            ProcessAccess.PROCESS_QUERY_LIMITED_INFORMATION);
    }

    /// <summary>
    /// Opens a process via NtOpenProcess — bypasses kernel32's ACL checks.
    /// Requires SeDebugPrivilege (which we enable before calling).
    /// </summary>
    private static IntPtr NtOpenProcessInternal(int pid, uint desiredAccess)
    {
        var oa = OBJECT_ATTRIBUTES.Create();
        var cid = new CLIENT_ID { UniqueProcess = (IntPtr)pid, UniqueThread = IntPtr.Zero };

        int status = NtdllImports.NtOpenProcess(
            out IntPtr hProcess,
            desiredAccess,
            ref oa,
            ref cid);

        return status == 0 ? hProcess : IntPtr.Zero;
    }

    /// <summary>
    /// Retrieves the full executable path for a process by PID.
    /// </summary>
    public static string? GetProcessPath(int pid)
    {
        try
        {
            IntPtr hProcess = Kernel32Imports.OpenProcess(
                ProcessAccess.PROCESS_QUERY_LIMITED_INFORMATION,
                false,
                (uint)pid);

            if (hProcess == IntPtr.Zero)
            {
                // Try NtOpenProcess
                hProcess = NtOpenProcessInternal(pid, ProcessAccess.PROCESS_QUERY_LIMITED_INFORMATION);
            }

            if (hProcess == IntPtr.Zero)
                return null;

            try
            {
                char[] buffer = new char[512];
                uint size = (uint)buffer.Length;
                if (Kernel32Imports.QueryFullProcessImageName(hProcess, 0, buffer, ref size))
                {
                    return new string(buffer, 0, (int)size);
                }
            }
            finally
            {
                NtdllImports.NtClose(hProcess);
            }
        }
        catch
        {
            // Process may be protected or inaccessible
        }

        return null;
    }
}
