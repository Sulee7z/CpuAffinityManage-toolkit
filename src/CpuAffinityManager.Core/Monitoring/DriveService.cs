using System.Runtime.Versioning;

namespace CpuAffinityManager.Monitoring;

/// <summary>
/// Enumerates the machine's drives so rules and the UI can target "all drives"
/// automatically instead of hard-coding a single drive letter (e.g. "D:\").
///
/// For drive-agnostic rules the recommended approach is a path pattern that starts
/// with <c>**</c> (matches any drive and any depth), e.g. <c>**\steamapps\common\**</c>
/// or <c>**\Games\**</c>. This service additionally lets callers expand a per-drive
/// template across every fixed drive when an explicit list is preferred.
/// </summary>
public static class DriveService
{
    /// <summary>
    /// Returns the root of every ready drive (e.g. "C:\", "D:\").
    /// </summary>
    public static IReadOnlyList<string> GetAllDriveRoots()
    {
        var roots = new List<string>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try { if (d.IsReady) roots.Add(d.RootDirectory.FullName); }
                catch { }
            }
        }
        catch { }
        return roots;
    }

    /// <summary>
    /// Returns the root of fixed (non-removable, non-network) drives only.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<string> GetFixedDriveRoots()
    {
        var roots = new List<string>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try { if (d.IsReady && d.DriveType == DriveType.Fixed) roots.Add(d.RootDirectory.FullName); }
                catch { }
            }
        }
        catch { }
        return roots;
    }

    /// <summary>
    /// Expands a per-drive relative template across all drives. For example
    /// <c>ExpandAcrossDrives("Games\\**")</c> yields "C:\Games\**", "D:\Games\**", …
    /// Useful when a caller wants explicit per-drive patterns instead of a leading **.
    /// </summary>
    public static IReadOnlyList<string> ExpandAcrossDrives(string relativeTemplate, bool fixedOnly = true)
    {
        relativeTemplate = relativeTemplate.TrimStart('\\', '/');
        var roots = fixedOnly && OperatingSystem.IsWindows()
            ? GetFixedDriveRoots()
            : GetAllDriveRoots();

        var result = new List<string>(roots.Count);
        foreach (var root in roots)
            result.Add(Path.Combine(root, relativeTemplate));
        return result;
    }
}
