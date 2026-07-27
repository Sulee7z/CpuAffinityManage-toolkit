using System.Text.Json.Serialization;

namespace CpuAffinityManager.Engine;

/// <summary>
/// A single affinity rule with match conditions and an action.
/// </summary>
public class RuleEntry
{
    /// <summary>Unique rule identifier (e.g., "rule-001").</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Human-readable rule name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether this rule is active.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Match conditions for the rule.</summary>
    [JsonPropertyName("match")]
    public RuleMatch Match { get; set; } = new();

    /// <summary>Action to apply when rule matches.</summary>
    [JsonPropertyName("action")]
    public RuleAction Action { get; set; } = new();
}

/// <summary>
/// Match conditions for a rule — process name, path, and exclusions.
/// </summary>
public class RuleMatch
{
    /// <summary>Wildcard pattern for process name (e.g., "game*.exe"). Required.</summary>
    [JsonPropertyName("process")]
    public string Process { get; set; } = string.Empty;

    /// <summary>Wildcard path pattern (e.g., "D:\\Games\\**"). Optional — null/empty matches any path.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>Patterns to exclude even if process name matches.</summary>
    [JsonPropertyName("exclude")]
    public List<string>? Exclude { get; set; }
}

/// <summary>
/// Action to apply when a rule matches a process.
/// </summary>
public class RuleAction
{
    /// <summary>Action type (currently only "cpu-affinity").</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "cpu-affinity";

    /// <summary>Affinity mode. Supports single modes (p-cores, first-half, etc.),
    /// composite fallback chains with | separator (e.g. "p-cores|first-half"),
    /// socket filter suffix (e.g. "p-cores@socket0"), or "custom".</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "all-cores";

<<<<<<< HEAD
    /// <summary>Enforcement level: soft-cpu-sets, hard-affinity, job-enforced, job-locked.</summary>
=======
    /// <summary>Enforcement level: soft-cpu-sets, hard-affinity, job-enforced.</summary>
>>>>>>> 07cba14d22092822ae57767f12fbf81c1eb1cba7
    [JsonPropertyName("level")]
    public string Level { get; set; } = "hard-affinity";

    /// <summary>Optional custom affinity mask as a hex string (e.g., "0xFF").
    /// Only used when mode is "custom".</summary>
    [JsonPropertyName("customMask")]
    public string? CustomMask { get; set; }

    /// <summary>
    /// Optional physical CPU socket index (0-based). When set, the affinity mask
    /// is restricted to cores on that specific physical CPU package.
    /// Omit or set to -1 to use all sockets.
    /// </summary>
    [JsonPropertyName("socketIndex")]
    public int? SocketIndex { get; set; }

    /// <summary>CPU priority class hint: low, belowNormal, normal, aboveNormal, high, realtime.</summary>
    [JsonPropertyName("cpuPriority")]
    public string? CpuPriority { get; set; }

    /// <summary>Optional IO priority: verylow, low, normal, high.</summary>
    [JsonPropertyName("ioPriority")]
    public string? IoPriority { get; set; }

    /// <summary>Optional memory priority: 1 (very low) … 5 (normal).</summary>
    [JsonPropertyName("memoryPriority")]
    public int? MemoryPriority { get; set; }

    /// <summary>When true, put the process into efficiency mode (EcoQoS).</summary>
    [JsonPropertyName("efficiencyMode")]
    public bool EfficiencyMode { get; set; }

    /// <summary>
<<<<<<< HEAD
    /// GPU scheduling priority class (0=idle, 1=belowNormal, 2=normal, 3=aboveNormal, 4=high, 5=realtime).
    /// Uses D3DKMTSetProcessSchedulingPriorityClass. null = not set.
    /// </summary>
    [JsonPropertyName("gpuPriority")]
    public int? GpuPriority { get; set; }

    /// <summary>
=======
>>>>>>> 07cba14d22092822ae57767f12fbf81c1eb1cba7
    /// Legacy single "优先核心" hint (kept for old rule files). Superseded by
    /// <see cref="PreferredCores"/>; folded into <see cref="GetPreferredMask"/>.
    /// </summary>
    [JsonPropertyName("preferredCore")]
    public int? PreferredCore { get; set; }

    /// <summary>
    /// "优先调度核心" (multi-select) as a hex bitmask string (e.g. "0x3" = cores 0+1).
    /// The process keeps its full affinity (can run on every core allowed by
    /// <see cref="Mode"/>) but its threads' ideal processors are spread round-robin
    /// over these cores, so Windows schedules them there first. null/empty = off.
    /// </summary>
    [JsonPropertyName("preferredCores")]
    public string? PreferredCores { get; set; }

<<<<<<< HEAD
    /// <summary>
    /// Scheduling pool bitmask (hex string). Cores in this mask are available for
    /// scheduling. Must be a superset of PreferredCores. null = all cores from Mode.
    /// </summary>
    [JsonPropertyName("schedulingPool")]
    public string? SchedulingPool { get; set; }

    /// <summary>
    /// Binding mode for priority-core scheduling:
    /// "dynamic" = soft IdealProcessor hints only (all cores usable)
    /// "static" = hard affinity to priority cores
    /// "d2"     = CPU Sets to priority cores + IdealProcessor
    /// "d3"     = IdealProcessor to priority cores + EcoQoS (省电)
    /// null/"dynamic" = default soft behavior.
    /// </summary>
    [JsonPropertyName("preferMode")]
    public string? PreferMode { get; set; }

=======
>>>>>>> 07cba14d22092822ae57767f12fbf81c1eb1cba7
    /// <summary>Combined preferred-core bitmask from PreferredCores (new) or PreferredCore (legacy).</summary>
    public ulong GetPreferredMask()
    {
        if (!string.IsNullOrWhiteSpace(PreferredCores))
        {
            string hex = PreferredCores.Trim();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
            if (ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out ulong m) && m != 0)
                return m;
        }
        if (PreferredCore is int pc && pc is >= 0 and < 64)
            return 1UL << pc;
        return 0;
    }

<<<<<<< HEAD
    /// <summary>Parses SchedulingPool hex string to a ulong bitmask. Returns 0 if not set.</summary>
    public ulong GetSchedulingPoolMask()
    {
        if (!string.IsNullOrWhiteSpace(SchedulingPool))
        {
            string hex = SchedulingPool.Trim();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
            if (ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out ulong m))
                return m;
        }
        return 0;
    }

    /// <summary>Effective prefer mode, defaults to "dynamic".</summary>
    public string GetPreferMode() =>
        !string.IsNullOrWhiteSpace(PreferMode) ? PreferMode : "dynamic";

    /// <summary>When true, also prevents the process from breaking away from
    /// the Job Object. Used with job-enforced level.</summary>
    [JsonPropertyName("lock")]
    public bool Lock { get; set; }

=======
>>>>>>> 07cba14d22092822ae57767f12fbf81c1eb1cba7
    /// <summary>
    /// Parses the CustomMask hex string to a ulong bitmask.
    /// </summary>
    public ulong? GetCustomMask()
    {
        if (string.IsNullOrWhiteSpace(CustomMask))
            return null;

        string hex = CustomMask.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            hex = hex[2..];

        if (ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out ulong mask))
            return mask;

        return null;
    }
}
