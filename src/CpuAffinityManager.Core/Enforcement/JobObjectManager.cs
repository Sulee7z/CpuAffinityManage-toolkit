using System.Runtime.InteropServices;
using CpuAffinityManager.Native;

namespace CpuAffinityManager.Enforcement;

/// <summary>
/// Manages the lifecycle of Job Objects used for CPU affinity enforcement.
/// Job Objects are kernel objects — they persist even after this process exits,
/// providing durable CPU affinity limits.
/// </summary>
public class JobObjectManager : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<int, IntPtr> _pidToJob = new();
    private bool _disposed;

    /// <summary>
    /// Creates or retrieves a named Job Object for a process.
    /// The Job Object name ensures uniqueness and allows re-attachment
    /// across tool restarts.
    /// </summary>
    public IntPtr GetOrCreateJob(int pid)
    {
        lock (_lock)
        {
            if (_pidToJob.TryGetValue(pid, out IntPtr existing))
                return existing;

            string jobName = $"LzxCpuAffinity_Job_{pid}";
            IntPtr hJob = Kernel32Imports.CreateJobObject(IntPtr.Zero, jobName);

            if (hJob != IntPtr.Zero)
            {
                _pidToJob[pid] = hJob;
            }

            return hJob;
        }
    }

    /// <summary>
    /// Sets the CPU affinity limit on a Job Object.
    /// </summary>
    public bool SetCpuAffinityLimit(IntPtr hJob, ulong mask)
    {
        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JobLimitFlags.JOB_OBJECT_LIMIT_AFFINITY,
                Affinity = (UIntPtr)mask
            }
        };

        int structSize = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr ptr = Marshal.AllocHGlobal(structSize);
        try
        {
            Marshal.StructureToPtr(limits, ptr, false);
            return Kernel32Imports.SetInformationJobObject(
                hJob,
                JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                ptr,
                (uint)structSize);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>
    /// Prevents processes in the Job from breaking away (creating child processes
    /// outside the Job).
    /// </summary>
    public bool PreventBreakaway(IntPtr hJob)
    {
        // Breakaway is denied by default unless BREAKAWAY_OK or
        // SILENT_BREAKAWAY_OK is explicitly set on the job. Keep the existing
        // limits intact; SetCpuAffinityLimit already writes the affinity limit.
        return hJob != IntPtr.Zero;
    }

    /// <summary>
    /// Assigns a process to a Job Object.
    /// </summary>
    public bool AssignProcess(IntPtr hJob, IntPtr hProcess)
    {
        return Kernel32Imports.AssignProcessToJobObject(hJob, hProcess);
    }

    /// <summary>
    /// Gets the Job Object handle for a process, if one exists.
    /// </summary>
    public IntPtr? GetJobForPid(int pid)
    {
        lock (_lock)
        {
            if (_pidToJob.TryGetValue(pid, out IntPtr hJob))
                return hJob;
            return null;
        }
    }

    /// <summary>
    /// Removes the tracking entry for a process (call when process exits).
    /// </summary>
    public void ReleaseJob(int pid)
    {
        lock (_lock)
        {
            if (_pidToJob.TryGetValue(pid, out IntPtr hJob))
            {
                Kernel32Imports.CloseHandle(hJob);
                _pidToJob.Remove(pid);
            }
        }
    }

    /// <summary>
    /// Clears the CPU affinity limit on every tracked Job Object (restores the full
    /// processor mask and drops the AFFINITY limit flag) and then closes all handles.
    /// Call this on application shutdown so processes that were affinity-limited by
    /// this tool — including system processes — are NOT left with a lingering kernel
    /// restriction after the app exits.
    /// </summary>
    public void ReleaseAllLimitsAndDispose()
    {
        if (_disposed) return;

        lock (_lock)
        {
            foreach (var hJob in _pidToJob.Values)
            {
                try { ClearAffinityLimit(hJob); } catch { }
                Kernel32Imports.CloseHandle(hJob);
            }
            _pidToJob.Clear();
            _disposed = true;
        }
    }

    /// <summary>
    /// Removes the affinity limit from a Job Object by writing an empty LimitFlags set.
    /// </summary>
    private static bool ClearAffinityLimit(IntPtr hJob)
    {
        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = 0,          // no limits
                Affinity = UIntPtr.Zero
            }
        };

        int structSize = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr ptr = Marshal.AllocHGlobal(structSize);
        try
        {
            Marshal.StructureToPtr(limits, ptr, false);
            return Kernel32Imports.SetInformationJobObject(
                hJob,
                JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                ptr,
                (uint)structSize);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            foreach (var hJob in _pidToJob.Values)
            {
                Kernel32Imports.CloseHandle(hJob);
            }
            _pidToJob.Clear();
        }
    }
}
