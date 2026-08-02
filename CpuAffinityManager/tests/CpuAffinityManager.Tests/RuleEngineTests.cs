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
    public void DefaultRules_CpuZRule_IsDisabledAndDoesNotMatch()
    {
        var engine = new RuleEngine();
        engine.Load(RuleConfigPath.FindDefaultRules(AppContext.BaseDirectory));

        // The CPU-Z anti-tamper rule ships DISABLED (rule-015), so it must not match.
        var result = engine.Match("CPU-Z-v2.08.0-CN.exe", @"J:\Tools\CPUZ\CPU-Z-v2.08.0-CN.exe");

        Assert.Null(result);
    }

    [Fact]
    public void DefaultRules_GameRule_MatchesWithPCoresAndJobEnforced()
    {
        var engine = new RuleEngine();
        engine.Load(RuleConfigPath.FindDefaultRules(AppContext.BaseDirectory));

        // rule-003 = 通用游戏目录 ("**\Games\**", p-cores|all-cores, job-enforced).
        var result = engine.Match("game.exe", @"J:\Games\SomeGame\game.exe");

        Assert.NotNull(result);
        Assert.Equal("rule-003", result.Id);
        Assert.Equal("p-cores|all-cores", result.Action.Mode);
        Assert.Equal("job-enforced", result.Action.Level);
    }

    [Fact]
    public void ExportJson_ImportJson_RoundTripsRules()
    {
        var engine = CreateEngineWithRules();
        string json = engine.ExportJson();
        Assert.Contains("rule-001", json);

        var other = new RuleEngine();
        other.Load(RuleConfigPath.FindDefaultRules(AppContext.BaseDirectory));
        int imported = other.ImportJson(json, replace: true);

        Assert.Equal(3, imported);
        Assert.Equal(3, other.Rules.Count);
        Assert.Equal("rule-001", other.Rules[0].Id);
    }

    [Fact]
    public void ImportJson_Merge_UpsertsAndKeepsOthers()
    {
        var engine = CreateEngineWithRules(); // 3 rules
        string incoming = """
            {"version":2,"rules":[
              {"id":"rule-001","name":"Merged Rule 001","enabled":true,
               "match":{"process":"game*.exe"},"action":{"mode":"e-cores","level":"hard-affinity"}},
              {"id":"rule-999","name":"Brand New","enabled":true,
               "match":{"process":"new*.exe"},"action":{"mode":"first-half","level":"soft-cpu-sets"}}
            ]}
            """;

        int imported = engine.ImportJson(incoming, replace: false);

        Assert.Equal(2, imported);
        Assert.Equal(4, engine.Rules.Count); // 3 original + 1 new (1 overwritten)
        Assert.Equal("Merged Rule 001", engine.Rules[0].Name);
        Assert.Equal("e-cores", engine.Rules[0].Action.Mode);
        Assert.True(engine.Rules.Any(r => r.Id == "rule-999"));
    }

    [Fact]
    public void ImportJson_InvalidJson_Throws()
    {
        var engine = CreateEngineWithRules();
        Assert.ThrowsAny<Exception>(() => engine.ImportJson("this is not json", replace: true));
    }

    [Fact]
    public void ImportJson_EmptyInput_Throws()
    {
        var engine = CreateEngineWithRules();
        Assert.Throws<ArgumentException>(() => engine.ImportJson("", replace: true));
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
