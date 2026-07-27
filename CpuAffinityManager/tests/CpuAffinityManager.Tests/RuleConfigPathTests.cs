using CpuAffinityManager.Engine;

namespace CpuAffinityManager.Tests;

public class RuleConfigPathTests
{
    [Fact]
    public void FindDefaultRules_WalksUpToProjectConfigFolder()
    {
        string root = Path.Combine(Path.GetTempPath(), "pm-rules-" + Guid.NewGuid().ToString("N"));
        string nested = Path.Combine(root, "src", "App", "bin", "Debug", "net10.0-windows");
        string config = Path.Combine(root, "config");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(config);

        string expected = Path.Combine(config, RuleConfigPath.DefaultFileName);
        File.WriteAllText(expected, "{}");

        try
        {
            Assert.Equal(expected, RuleConfigPath.FindDefaultRules(nested));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
