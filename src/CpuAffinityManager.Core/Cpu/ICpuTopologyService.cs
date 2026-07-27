namespace CpuAffinityManager.Cpu;

/// <summary>
/// Service for detecting and querying CPU topology.
/// Supports composite/fallback modes and multi-socket filtering.
/// </summary>
public interface ICpuTopologyService
{
    /// <summary>
    /// Detects the current system's CPU topology. Results are cached.
    /// </summary>
    CpuTopology Detect();

    /// <summary>
    /// Builds an affinity mask for the given mode string.
    ///
    /// Supports:
    /// <list type="bullet">
    ///   <item>Single modes: <c>"p-cores"</c>, <c>"first-half"</c>, etc.</item>
    ///   <item>Composite fallback chains: <c>"p-cores|first-half"</c>
    ///         (tries each in order, returns first non-zero mask)</item>
    ///   <item>Socket filter suffix: <c>"p-cores@socket0"</c></item>
    ///   <item>Custom mask: <c>"custom"</c> (uses <paramref name="customMask"/>)</item>
    /// </list>
    /// </summary>
    /// <param name="mode">Mode string (may include | fallback chains and @socket suffix).</param>
    /// <param name="customMask">Required when mode is "custom".</param>
    ulong BuildMask(string mode, ulong? customMask = null);

    /// <summary>
    /// Returns a list of all valid single-mode names (for UI dropdowns).
    /// </summary>
    static IReadOnlyList<string> AvailableModes { get; } = new[]
    {
        "all-cores", "p-cores", "e-cores", "p-cores-smt",
        "p-cores-no-smt", "first-half", "second-half", "custom"
    };
}
