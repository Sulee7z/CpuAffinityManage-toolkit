using System.Text;

namespace CpuAffinityManager.Monitoring;

/// <summary>
/// Search helper for process list filtering.
/// </summary>
public static class ProcessSearch
{
    public static bool Matches(string? query, string name, string? path, int pid)
    {
        query = query?.Trim();
        if (string.IsNullOrEmpty(query))
            return true;

        if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrEmpty(path) &&
            path.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;

        if (pid.ToString().Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;

        return Normalize(name).Contains(Normalize(query), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
