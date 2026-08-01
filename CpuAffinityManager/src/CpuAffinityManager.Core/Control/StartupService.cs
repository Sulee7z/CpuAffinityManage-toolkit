using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace CpuAffinityManager.ProcOps;

/// <summary>A registry-based startup (auto-run) entry.</summary>
public sealed class StartupEntry
{
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string Hive { get; init; } = "";      // "HKCU" or "HKLM"
    public string Location => Hive == "HKLM" ? "所有用户" : "当前用户";
}

/// <summary>
/// Lists and removes Windows startup (Run-key) entries via reg.exe — no extra NuGet
/// dependency. Covers per-user (HKCU) and machine-wide (HKLM) Run keys.
/// </summary>
[SupportedOSPlatform("windows")]
public static class StartupService
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string SelfName = "CpuAffinityManager";

    /// <summary>Whether THIS application is registered to run at user logon.</summary>
    public static bool IsSelfStartupEnabled()
    {
        string outp = RunOut("reg.exe", $"query \"HKCU\\{RunKey}\" /v {SelfName}");
        return outp.IndexOf(SelfName, StringComparison.OrdinalIgnoreCase) >= 0
            && outp.IndexOf("REG_SZ", StringComparison.Ordinal) >= 0;
    }

    /// <summary>Enables/disables running THIS application at user logon (HKCU Run key).</summary>
    public static bool SetSelfStartup(bool enable)
    {
        if (!enable)
        {
            RunOut("reg.exe", $"delete \"HKCU\\{RunKey}\" /v {SelfName} /f");
            return true;
        }
        string exe = Environment.ProcessPath ?? "";
        if (string.IsNullOrEmpty(exe)) return false;
        // Store the quoted path so paths with spaces launch correctly.
        string outp = RunOut("reg.exe", $"add \"HKCU\\{RunKey}\" /v {SelfName} /t REG_SZ /d \"\\\"{exe}\\\"\" /f");
        return outp.IndexOf("成功", StringComparison.Ordinal) >= 0
            || outp.IndexOf("success", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static List<StartupEntry> List()
    {
        var result = new List<StartupEntry>();
        result.AddRange(Query("HKCU", $@"HKCU\{RunKey}"));
        result.AddRange(Query("HKLM", $@"HKLM\{RunKey}"));
        return result;
    }

    private static IEnumerable<StartupEntry> Query(string hive, string fullKey)
    {
        string outp = RunOut("reg.exe", $"query \"{fullKey}\"");
        foreach (var raw in outp.Split('\n'))
        {
            // "    Name    REG_SZ    C:\path app.exe"
            var m = Regex.Match(raw, @"^\s+(\S.*?)\s{2,}(REG_(?:SZ|EXPAND_SZ))\s{2,}(.*)$");
            if (!m.Success) continue;
            string name = m.Groups[1].Value.Trim();
            if (name.Length == 0 || name.StartsWith("HK")) continue; // skip the key path line
            yield return new StartupEntry { Name = name, Command = m.Groups[3].Value.Trim(), Hive = hive };
        }
    }

    /// <summary>Removes a startup entry. Returns true on success (HKLM needs admin).</summary>
    public static bool Remove(string hive, string name)
    {
        string fullKey = $@"{hive}\{RunKey}";
        string outp = RunOut("reg.exe", $"delete \"{fullKey}\" /v \"{name}\" /f");
        return outp.IndexOf("成功", StringComparison.Ordinal) >= 0
            || outp.IndexOf("success", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string RunOut(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            string o = p.StandardOutput.ReadToEnd();
            string e = p.StandardError.ReadToEnd();
            p.WaitForExit(6000);
            return o + "\n" + e;
        }
        catch { return ""; }
    }
}
