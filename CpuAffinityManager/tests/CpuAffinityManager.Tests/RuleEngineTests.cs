using CpuAffinityManager.Engine;

namespace CpuAffinityManager.Tests;

public class RuleEngineTests
{
    private static RuleEngine CreateEngineWithRules()
    {
        var engine = new RuleEngine();
        engine.AddRule(new RuleEntry
        {
            Id = "rule-001",
            Name = "Games on D drive",
            Enabled = true,
            Match = new RuleMatch
            {
                Process = "*.exe",
                Path = @"D:\\Games\\**",
                Exclude = new List<string> { "*launcher*.exe" }
            },
            Action = new RuleAction { Mode = "p-cores", Level = "job-enforced" }
        });
        engine.AddRule(new RuleEntry
        {
            Id = "rule-002",
            Name = "CPU-Z anti-tamper",
            Enabled = true,
            Match = new RuleMatch
            {
                Process = "cpuz*.exe|cpu-z*.exe"
            },
            Action = new RuleAction { Mode = "all-cores", Level = "job-locked", Lock = true }
        });
        engine.AddRule(new RuleEntry
        {
            Id = "rule-003",
            Name = "Disabled rule",
            Enabled = false,
            Match = new RuleMatch { Process = "*.exe" },
            Action = new RuleAction { Mode = "e-cores" }
        });
        return engine;
    }

    [Fact]
    public void Match_FirstRuleWins_ReturnsCorrectRule()
    {
        var engine = CreateEngineWithRules();
        var result = engine.Match("game.exe", @"D:\Games\Steam\game.exe");
        Assert.NotNull(result);
        Assert.Equal("rule-001", result.Id);
    }

    [Fact]
    public void Match_OrPattern_MatchesSecondAlternative()
    {
        var engine = CreateEngineWithRules();
        var result = engine.Match("cpu-z_x64.exe", @"C:\Tools\cpu-z_x64.exe");
        Assert.NotNull(result);
        Assert.Equal("rule-002", result.Id);
    }

    [Fact]
    public void DefaultRules_CpuZRule_UsesECoresWithLockedJob()
    {
        var engine = new RuleEngine();
        engine.Load(RuleConfigPath.FindDefaultRules(AppContext.BaseDirectory));

        var result = engine.Match("CPU-Z-v2.08.0-CN.exe", @"J:\Tools\CPUZ\CPU-Z-v2.08.0-CN.exe");

        Assert.NotNull(result);
        Assert.Equal("rule-003", result.Id);
        Assert.Equal("e-cores|second-half", result.Action.Mode);
        Assert.Equal("job-locked", result.Action.Level);
        Assert.True(result.Action.Lock);
    }

    [Fact]
    public void Match_ExcludedProcess_ReturnsNull()
    {
        var engine = CreateEngineWithRules();
        var result = engine.Match("gamelauncher.exe", @"D:\Games\gamelauncher.exe");
        Assert.Null(result);
    }

    [Fact]
    public void Match_WrongPath_ReturnsNull()
    {
        var engine = CreateEngineWithRules();
        var result = engine.Match("game.exe", @"C:\Other\game.exe");
        // Rule-001 requires D:\Games\**, so it shouldn't match.
        // Rule-002 requires cpuz pattern, so it shouldn't match.
        Assert.Null(result);
    }

    [Fact]
    public void Match_DisabledRule_IsSkipped()
    {
        var engine = new RuleEngine();
        engine.AddRule(new RuleEntry
        {
            Id = "disabled-rule",
            Enabled = false,
            Match = new RuleMatch { Process = "*.exe" },
            Action = new RuleAction { Mode = "all-cores" }
        });

        // With no enabled rules, nothing should match
        var result = engine.Match("test.exe", @"C:\test.exe");
        Assert.Null(result);
    }

    [Fact]
    public void AddRule_DuplicateId_ReplacesExisting()
    {
        var engine = new RuleEngine();
        engine.AddRule(new RuleEntry
        {
            Id = "test-rule",
            Name = "Original",
            Enabled = true,
            Match = new RuleMatch { Process = "*.exe" },
            Action = new RuleAction { Mode = "p-cores" }
        });
        engine.AddRule(new RuleEntry
        {
            Id = "test-rule",
            Name = "Updated",
            Enabled = true,
            Match = new RuleMatch { Process = "*.dll" },
            Action = new RuleAction { Mode = "e-cores" }
        });

        Assert.Single(engine.Rules);
        Assert.Equal("Updated", engine.Rules[0].Name);
    }

    [Fact]
    public void RemoveRule_ExistingId_ReturnsTrue()
    {
        var engine = CreateEngineWithRules();
        bool removed = engine.RemoveRule("rule-001");
        Assert.True(removed);
        Assert.Equal(2, engine.Rules.Count);
    }

    [Fact]
    public void RemoveRule_NonExistingId_ReturnsFalse()
    {
        var engine = CreateEngineWithRules();
        bool removed = engine.RemoveRule("nonexistent");
        Assert.False(removed);
        Assert.Equal(3, engine.Rules.Count);
    }
}
