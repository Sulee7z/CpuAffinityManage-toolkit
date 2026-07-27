namespace CpuAffinityManager.Cpu;

/// <summary>
/// Detected CPU topology including P-core/E-core masks, SMT layout,
/// per-socket masks for multi-CPU systems, and composite mask builders.
/// </summary>
public class CpuTopology
{
    /// <summary>Total number of logical processors in the system.</summary>
    public int TotalLogicalProcessors { get; init; }

    /// <summary>Number of performance cores (P-cores).</summary>
    public int PcoreCount { get; init; }

    /// <summary>Number of efficiency cores (E-cores).</summary>
    public int EcoreCount { get; init; }

    /// <summary>Whether SMT (Hyper-Threading) is enabled.</summary>
    public bool SmtEnabled { get; init; }

    /// <summary>Bitmask of all P-core logical processors.</summary>
    public ulong PcoreMask { get; init; }

    /// <summary>Bitmask of all E-core logical processors.</summary>
    public ulong EcoreMask { get; init; }

    /// <summary>Bitmask of physical threads (SMT0) only.</summary>
    public ulong Smt0Mask { get; init; }

    /// <summary>Bitmask of hyper-threads (SMT1) only.</summary>
    public ulong Smt1Mask { get; init; }

    /// <summary>Bitmask of AMD CCD0 cores (0 on Intel systems).</summary>
    public ulong Ccd0Mask { get; init; }

    /// <summary>Bitmask of AMD CCD1 cores (0 on Intel systems).</summary>
    public ulong Ccd1Mask { get; init; }

    /// <summary>Number of physical CPU sockets/packages detected.</summary>
    public int SocketCount { get; init; }

    /// <summary>Per-socket bitmasks. Index 0 = first physical CPU, etc.</summary>
    public List<ulong> SocketMasks { get; init; } = new();

    /// <summary>
    /// Predefined affinity mask builders keyed by mode name.
    /// </summary>
    public static readonly Dictionary<string, Func<CpuTopology, ulong>> MaskBuilders = new()
    {
        ["all-cores"]       = t => ~0UL,
        ["p-cores"]         = t => t.PcoreMask,
        ["e-cores"]         = t => t.EcoreMask,
        ["p-cores-smt"]     = t => t.PcoreMask,
        ["p-cores-no-smt"]  = t => t.PcoreMask & ~t.Smt1Mask,
        ["p-cores-first"]   = t => t.Smt0Mask & t.PcoreMask,
        ["first-half"]      = t => BuildHalfMask(t, firstHalf: true),
        ["second-half"]     = t => BuildHalfMask(t, firstHalf: false),
    };

    /// <summary>
    /// Builds an affinity mask from a mode string, supporting composite/fallback
    /// chains with | separator (e.g., "p-cores|first-half" tries p-cores first,
    /// falls back to first-half if p-cores mask is 0).
    ///
    /// Also supports socket filter suffix: "p-cores@socket0".
    /// </summary>
    public static ulong BuildMask(CpuTopology topology, string mode, ulong? customMask = null)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return 0;

        // Handle custom mask mode
        if (mode == "custom")
            return ClampToLogicalProcessors(customMask ?? 0, topology.TotalLogicalProcessors);

        // Parse socket suffix: "mode@socket0" or "mode@socket1"
        int socketIdx = -1;
        int atIdx = mode.IndexOf('@');
        string modePart = mode;
        if (atIdx > 0)
        {
            modePart = mode[..atIdx];
            string socketStr = mode[(atIdx + 1)..];
            if (socketStr.StartsWith("socket", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(socketStr[6..], out int si))
            {
                socketIdx = si;
            }
        }

        // Handle composite/fallback chain: "mode1|mode2|mode3"
        if (modePart.Contains('|'))
        {
            var fallbacks = modePart.Split('|', StringSplitOptions.TrimEntries);
            foreach (var fallback in fallbacks)
            {
                ulong mask = BuildSingleMask(topology, fallback, customMask);
                if (mask != 0)
                    return ClampToLogicalProcessors(
                        ApplySocketFilter(mask, topology, socketIdx),
                        topology.TotalLogicalProcessors);
            }
            return 0;
        }

        ulong result = BuildSingleMask(topology, modePart, customMask);
        return ClampToLogicalProcessors(
            ApplySocketFilter(result, topology, socketIdx),
            topology.TotalLogicalProcessors);
    }

    public static ulong ClampToLogicalProcessors(ulong mask, int totalLogicalProcessors)
    {
        if (totalLogicalProcessors <= 0)
            return 0;

        if (totalLogicalProcessors >= 64)
            return mask;

        return mask & ((1UL << totalLogicalProcessors) - 1);
    }

    /// <summary>
    /// Builds a single-mode mask (no fallback chains).
    /// </summary>
    private static ulong BuildSingleMask(CpuTopology topology, string mode, ulong? customMask)
    {
        if (MaskBuilders.TryGetValue(mode, out var builder))
            return builder(topology);

        return 0;
    }

    /// <summary>
    /// Intersects the mask with a specific socket's core mask if socketIndex is valid.
    /// </summary>
    private static ulong ApplySocketFilter(ulong mask, CpuTopology topology, int socketIndex)
    {
        if (socketIndex < 0 || topology.SocketMasks.Count == 0)
            return mask;

        if (socketIndex < topology.SocketMasks.Count)
            return mask & topology.SocketMasks[socketIndex];

        return 0; // Invalid socket index
    }

    /// <summary>
    /// Builds a mask covering either the first or second half of all logical processors.
    /// </summary>
    public static ulong BuildHalfMask(CpuTopology topology, bool firstHalf)
    {
        int half = topology.TotalLogicalProcessors / 2;
        if (half >= 64) half = 64;

        ulong mask = 0;
        int start = firstHalf ? 0 : half;
        int end = firstHalf ? half : topology.TotalLogicalProcessors;
        if (end > 64) end = 64;

        for (int i = start; i < end; i++)
            mask |= 1UL << i;

        return mask;
    }

    public override string ToString()
    {
        var parts = new List<string>
        {
            $"CPU: {TotalLogicalProcessors} Logical",
            $"{PcoreCount}P + {EcoreCount}E",
            $"SMT={(SmtEnabled ? "On" : "Off")}"
        };
        if (SocketCount > 1)
            parts.Add($"{SocketCount} Sockets");
        parts.Add($"P-mask=0x{PcoreMask:X}");
        parts.Add($"E-mask=0x{EcoreMask:X}");
        return string.Join(", ", parts);
    }
}
