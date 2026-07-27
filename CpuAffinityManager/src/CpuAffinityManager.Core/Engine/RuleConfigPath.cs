namespace CpuAffinityManager.Engine;

/// <summary>
/// Resolves the rules file location.
///
/// Writable data (the live rules file) is kept under
/// <c>%LOCALAPPDATA%\CpuAffinityManager\default-rules.json</c> — NOT inside the
/// application/install directory. Writing rules back into the program folder (which
/// may sit under Program Files or another ACL-restricted location) was the cause of
/// the "Windows 目录权限" problem: an elevated run would rewrite the file and change
/// its ACL so a later non-elevated run could no longer read or write it. Keeping the
/// live copy in the per-user LocalAppData folder avoids elevation-related ACL drift
/// entirely.
///
/// On first use the writable copy is seeded from the bundled template shipped in the
/// application's <c>config\default-rules.json</c>.
/// </summary>
public static class RuleConfigPath
{
    public const string DefaultFileName = "default-rules.json";
    public const string AppFolderName = "CpuAffinityManager";

    /// <summary>Per-user writable data directory (%LOCALAPPDATA%\CpuAffinityManager).</summary>
    public static string DataDirectory
    {
        get
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(root))
                root = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(root, AppFolderName);
        }
    }

    /// <summary>
    /// Returns the writable rules path under LocalAppData, creating the directory and
    /// seeding the file from the bundled template on first run.
    /// </summary>
    public static string FindDefaultRules(string? baseDirectory = null)
    {
        string dataDir = DataDirectory;
        string writablePath = Path.Combine(dataDir, DefaultFileName);

        try
        {
            Directory.CreateDirectory(dataDir);

            if (!File.Exists(writablePath))
            {
                string? template = FindBundledTemplate(baseDirectory);
                if (template != null && File.Exists(template))
                    File.Copy(template, writablePath);
            }
        }
        catch
        {
            // If LocalAppData is somehow unavailable, fall back to the bundled
            // template path so the app can still read (read-only) defaults.
            string? template = FindBundledTemplate(baseDirectory);
            if (template != null)
                return template;
        }

        return writablePath;
    }

    /// <summary>
    /// Locates the read-only template shipped alongside the executable
    /// (config\default-rules.json), searching the app dir and its parents.
    /// </summary>
    public static string? FindBundledTemplate(string? baseDirectory = null)
    {
        baseDirectory = Path.GetFullPath(baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory);

        foreach (string directory in EnumerateSelfAndParents(baseDirectory))
        {
            string candidate = Path.Combine(directory, "config", DefaultFileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateSelfAndParents(string directory)
    {
        var current = new DirectoryInfo(directory);
        while (current != null)
        {
            yield return current.FullName;
            current = current.Parent;
        }
    }
}
