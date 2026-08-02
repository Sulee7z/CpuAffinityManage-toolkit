using CpuAffinityManager.Engine;
using CpuAffinityManager.Cpu;

namespace CpuAffinityManager.Enforcement;

/// <summary>
/// Service for applying CPU affinity rules to processes using the appropriate
/// enforcement mechanism (soft CPU Sets, hard affinity, or Job Object).
/// </summary>
public interface IEnforcementService
{
    /// <summary>
    /// Applies a rule to a specific process by PID.
    /// </summary>
    /// <param name="pid">Target process ID.</param>
    /// <param name="rule">The matched rule to apply.</param>
    /// <param name="topology">Current CPU topology for mask building.</param>
    /// <returns>true if the enforcement was applied successfully.</returns>
    bool Apply(int pid, RuleEntry rule, CpuTopology topology);

    /// <summary>
    /// Removes this tool's affinity restriction from a process by widening any
    /// tracked Job Object and hard process affinity back to all logical CPUs.
    /// </summary>
    bool Relax(int pid, CpuTopology topology);

    /// <summary>
    /// Applies a Job Object-based CPU affinity limit to a process.
    /// Uses the most powerful enforcement level.
    /// </summary>
    /// <param name="pid">Target process ID.</param>
    /// <param name="mask">Affinity bitmask.</param>
    /// <returns>true if successful.</returns>
    bool ApplyJobEnforced(int pid, ulong mask);

    /// <summary>
    /// Scans all running processes and applies matching rules.
    /// </summary>
    /// <returns>Number of processes affected.</returns>
    int ScanAndEnforce();

    /// <summary>
    /// Releases every Job Object affinity limit this service created and disposes
    /// them. Call once on application shutdown so no kernel-level CPU restriction is
    /// left on any process (including Windows system processes) after the app exits.
    /// </summary>
    void ShutdownCleanup();

    /// <summary>
    /// Releases tracked Job Object handles whose PIDs are no longer alive, so the
    /// job table does not grow unboundedly and a reused PID never inherits a stale
    /// affinity limit. Call periodically with the set of currently live PIDs.
    /// </summary>
    void PruneDeadJobs(System.Collections.Generic.ISet<int> livePids);
}
