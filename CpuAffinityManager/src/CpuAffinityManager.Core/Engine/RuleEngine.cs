namespace CpuAffinityManager.Engine;

/// <summary>
/// Implements first-match-wins rule matching with wildcard support.
/// Thread-safe for reads; writes are serialized via lock.
/// </summary>
public class RuleEngine : IRuleEngine
{
    private readonly object _writeLock = new();

    // Immutable snapshot published via a single reference write. Readers (Match,
    // Rules) grab the current reference with no lock and no per-call copy; writers
    // build a new list and swap it in (copy-on-write). This removes the previous
    // per-process List allocation on the matching hot path.
    private volatile RuleEntry[] _rules = System.Array.Empty<RuleEntry>();

    public IReadOnlyList<RuleEntry> Rules => _rules;

    /// <summary>
    /// Matches a process against rules in order. Returns the first matching rule,
    /// or null if no rule matches. Disabled rules are skipped.
    /// </summary>
    public RuleEntry? Match(string processName, string fullPath)
    {
        if (string.IsNullOrEmpty(processName))
            return null;

        // Lock-free read of the current immutable snapshot.
        RuleEntry[] rules = _rules;

        foreach (var rule in rules)
        {
            if (!rule.Enabled)
                continue;

            // Process name is always required
            if (string.IsNullOrEmpty(rule.Match.Process))
                continue;

            if (!Wildcard.Match(processName, rule.Match.Process, ignoreCase: true))
                continue;

            // Path match is optional — if specified, must match
            if (!string.IsNullOrEmpty(rule.Match.Path) &&
                !Wildcard.MatchPath(fullPath, rule.Match.Path, ignoreCase: true))
                continue;

            // Exclude patterns — if any match, skip this rule
            var exclude = rule.Match.Exclude;
            if (exclude != null && exclude.Count > 0)
            {
                bool excluded = false;
                for (int i = 0; i < exclude.Count; i++)
                {
                    if (Wildcard.Match(processName, exclude[i], ignoreCase: true))
                    {
                        excluded = true;
                        break;
                    }
                }
                if (excluded)
                    continue;
            }

            // All conditions satisfied — first match wins
            return rule;
        }

        return null;
    }

    /// <summary>
    /// Loads rules from a JSON file.
    /// </summary>
    public void Load(string configPath)
    {
        var config = RuleConfig.Load(configPath);
        lock (_writeLock)
        {
            _rules = config.Rules?.ToArray() ?? System.Array.Empty<RuleEntry>();
        }
    }

    /// <summary>
    /// Saves current rules to a JSON file.
    /// </summary>
    public void Save(string configPath)
    {
        var config = new RuleConfig
        {
            Version = 2,
            Rules = new List<RuleEntry>(_rules)
        };
        config.Save(configPath);
    }

    /// <summary>
    /// Adds a rule. If a rule with the same ID exists, it is replaced.
    /// </summary>
    public void AddRule(RuleEntry rule)
    {
        lock (_writeLock)
        {
            var list = new List<RuleEntry>(_rules);
            int existingIndex = list.FindIndex(r => r.Id == rule.Id);
            if (existingIndex >= 0)
                list[existingIndex] = rule;
            else
                list.Add(rule);
            _rules = list.ToArray();
        }
    }

    /// <summary>
    /// Removes a rule by ID. Returns true if a rule was removed.
    /// </summary>
    public bool RemoveRule(string ruleId)
    {
        lock (_writeLock)
        {
            var list = new List<RuleEntry>(_rules);
            int removed = list.RemoveAll(r => r.Id == ruleId);
            if (removed == 0)
                return false;
            _rules = list.ToArray();
            return true;
        }
    }

    /// <summary>
    /// Serializes the current rules to a JSON string (same format as the rules
    /// file, including version/settings headers).
    /// </summary>
    public string ExportJson()
    {
        var config = new RuleConfig
        {
            Version = 2,
            Rules = new List<RuleEntry>(_rules)
        };
        return config.ToJson();
    }

    /// <summary>
    /// Imports rules from a JSON string (same format as the rules file).
    /// <paramref name="replace"/> = true replaces ALL current rules; false merges
    /// with existing rules (matching IDs are overwritten by the imported ones).
    /// Returns the number of imported rules. Throws on invalid JSON.
    /// </summary>
    public int ImportJson(string json, bool replace)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("导入内容为空");

        var config = RuleConfig.Parse(json);
        var incoming = config.Rules ?? new List<RuleEntry>();
        if (incoming.Count == 0)
            return 0;

        lock (_writeLock)
        {
            if (replace)
            {
                _rules = incoming.ToArray();
            }
            else
            {
                var merged = new List<RuleEntry>(_rules);
                foreach (var rule in incoming)
                {
                    if (string.IsNullOrWhiteSpace(rule.Id))
                        continue;
                    int existing = merged.FindIndex(r => r.Id == rule.Id);
                    if (existing >= 0)
                        merged[existing] = rule;
                    else
                        merged.Add(rule);
                }
                _rules = merged.ToArray();
            }
        }

        return incoming.Count;
    }
}
