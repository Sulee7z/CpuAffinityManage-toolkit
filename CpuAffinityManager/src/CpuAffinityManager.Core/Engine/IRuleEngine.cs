namespace CpuAffinityManager.Engine;

/// <summary>
/// Rule matching engine — matches processes against configured rules
/// using wildcard patterns with first-match-wins semantics.
/// </summary>
public interface IRuleEngine
{
    /// <summary>
    /// Finds the first matching rule for a process. Returns null if no rule matches.
    /// </summary>
    /// <param name="processName">Process executable name (e.g., "game.exe").</param>
    /// <param name="fullPath">Full path to the executable.</param>
    /// <returns>The matched RuleEntry, or null.</returns>
    RuleEntry? Match(string processName, string fullPath);

    /// <summary>
    /// Loads rules from a JSON configuration file.
    /// </summary>
    void Load(string configPath);

    /// <summary>
    /// Saves rules to a JSON configuration file.
    /// </summary>
    void Save(string configPath);

    /// <summary>
    /// Adds a new rule.
    /// </summary>
    void AddRule(RuleEntry rule);

    /// <summary>
    /// Removes a rule by its ID.
    /// </summary>
    /// <returns>true if the rule was found and removed.</returns>
    bool RemoveRule(string ruleId);

    /// <summary>
    /// Returns a read-only view of all rules.
    /// </summary>
    IReadOnlyList<RuleEntry> Rules { get; }
}
