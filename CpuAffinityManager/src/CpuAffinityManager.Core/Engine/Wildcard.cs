namespace CpuAffinityManager.Engine;

/// <summary>
/// Wildcard pattern matching engine supporting:
/// <list type="bullet">
///   <item><c>*</c> — matches any characters except path separator</item>
///   <item><c>**</c> — matches any characters including path separators (multi-level)</item>
///   <item><c>?</c> — matches exactly one character</item>
///   <item><c>|</c> — OR separator between alternative patterns</item>
///   <item><c>[chars]</c> — character class (ranges supported, e.g. <c>[0-9a-f]</c>)</item>
/// </list>
/// </summary>
public static class Wildcard
{
    /// <summary>
    /// Matches a filename or single-segment string against a pattern.
    /// Supports *, ?, | (OR), and [chars] character classes.
    /// </summary>
    public static bool Match(string input, string pattern, bool ignoreCase = true)
    {
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(pattern))
            return false;

        // Handle OR-separated patterns without allocating (hot path: called per
        // process per rule). Split the pattern span on '|' and test each slice.
        if (pattern.IndexOf('|') >= 0)
        {
            ReadOnlySpan<char> rest = pattern.AsSpan();
            while (!rest.IsEmpty)
            {
                int sep = rest.IndexOf('|');
                ReadOnlySpan<char> alt = sep < 0 ? rest : rest.Slice(0, sep);
                if (MatchSpan(input.AsSpan(), alt.Trim(), ignoreCase))
                    return true;
                if (sep < 0) break;
                rest = rest.Slice(sep + 1);
            }
            return false;
        }

        return MatchSpan(input.AsSpan(), pattern.AsSpan(), ignoreCase);
    }

    /// <summary>
    /// Matches a full file path against a path pattern.
    /// Uses ** for multi-level directory matching.
    /// Supports | OR alternatives between whole path patterns
    /// (e.g. "D:\Games\**|E:\Games\**").
    /// </summary>
    public static bool MatchPath(string path, string pattern, bool ignoreCase = true)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(pattern))
            return false;

        // OR-separated path alternatives: try each in order.
        if (pattern.IndexOf('|') >= 0)
        {
            var alternatives = pattern.Split('|', StringSplitOptions.TrimEntries);
            foreach (string alt in alternatives)
            {
                if (alt.Length == 0)
                    continue;
                if (MatchPath(path, alt, ignoreCase))
                    return true;
            }
            return false;
        }

        // Normalize separators
        path = path.Replace('/', '\\');
        pattern = pattern.Replace('/', '\\');

        // Split into segments (filtering empty entries from double-backslash etc.)
        var pathSegments = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var patternSegments = pattern.Split('\\', StringSplitOptions.RemoveEmptyEntries);

        return MatchSegments(pathSegments, 0, patternSegments, 0, ignoreCase);
    }

    /// <summary>
    /// Recursively matches path segments against pattern segments.
    /// ** matches zero or more path segments.
    /// </summary>
    private static bool MatchSegments(
        string[] path, int pi,
        string[] pattern, int ppi,
        bool ignoreCase)
    {
        while (ppi < pattern.Length)
        {
            if (pattern[ppi] == "**")
            {
                ppi++;
                // ** at end matches everything remaining
                if (ppi >= pattern.Length)
                    return true;

                // Try matching remaining pattern at each position in path
                for (int i = pi; i < path.Length; i++)
                {
                    if (MatchSegments(path, i, pattern, ppi, ignoreCase))
                        return true;
                }
                return false;
            }
            else
            {
                // Normal segment match
                if (pi >= path.Length)
                    return false;
                if (!MatchSingle(path[pi], pattern[ppi], ignoreCase))
                    return false;
                pi++;
                ppi++;
            }
        }

        // Pattern exhausted — must also be at end of path
        return pi >= path.Length;
    }

    /// <summary>
    /// Matches a single pattern alternative (no | splitting, no **).
    /// Uses greedy iterative matching with backtracking for *.
    /// </summary>
    private static bool MatchSingle(string input, string pattern, bool ignoreCase)
    {
        return MatchSpan(input.AsSpan(), pattern.AsSpan(), ignoreCase);
    }

    private static bool MatchSpan(ReadOnlySpan<char> input, ReadOnlySpan<char> pattern, bool ignoreCase)
    {
        int i = 0, p = 0;
        int starIdx = -1;
        int matchIdx = 0;

        while (i < input.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                // Record star for backtracking
                starIdx = p;
                matchIdx = i;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '?')
            {
                i++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '[')
            {
                int close = FindCloseBracket(pattern, p);
                if (close < 0)
                {
                    // No closing bracket, treat [ as literal
                    if (!CharEq(input[i], '[', ignoreCase))
                    {
                        if (starIdx < 0) return false;
                        p = starIdx + 1;
                        matchIdx++;
                        i = matchIdx;
                    }
                    else { i++; p++; }
                }
                else
                {
                    if (!MatchCharClass(input[i], pattern.Slice(p, close - p + 1), ignoreCase))
                    {
                        if (starIdx < 0) return false;
                        p = starIdx + 1;
                        matchIdx++;
                        i = matchIdx;
                    }
                    else { i++; p = close + 1; }
                }
            }
            else if (p < pattern.Length)
            {
                if (!CharEq(input[i], pattern[p], ignoreCase))
                {
                    if (starIdx < 0) return false;
                    p = starIdx + 1;
                    matchIdx++;
                    i = matchIdx;
                }
                else { i++; p++; }
            }
            else
            {
                // Pattern exhausted, input remains
                if (starIdx < 0) return false;
                p = starIdx + 1;
                matchIdx++;
                i = matchIdx;
            }
        }

        // Consume trailing *
        while (p < pattern.Length && pattern[p] == '*')
            p++;

        return p == pattern.Length;
    }

    /// <summary>
    /// Matches a character against [range] or [!range] class.
    /// </summary>
    private static bool MatchCharClass(char c, ReadOnlySpan<char> pattern, bool ignoreCase)
    {
        if (pattern.Length < 3 || pattern[0] != '[')
            return false;

        int idx = 1;
        bool negate = false;
        if (idx < pattern.Length && pattern[idx] == '!')
        {
            negate = true;
            idx++;
        }

        bool matched = false;
        while (idx < pattern.Length - 1)
        {
            if (idx + 2 < pattern.Length - 1 && pattern[idx + 1] == '-')
            {
                char lo = pattern[idx], hi = pattern[idx + 2];
                if (CharInRange(c, lo, hi, ignoreCase))
                    matched = true;
                idx += 3;
            }
            else
            {
                if (CharEq(c, pattern[idx], ignoreCase))
                    matched = true;
                idx++;
            }
        }

        return negate ? !matched : matched;
    }

    private static int FindCloseBracket(ReadOnlySpan<char> pattern, int start)
    {
        for (int i = start + 1; i < pattern.Length; i++)
            if (pattern[i] == ']') return i;
        return -1;
    }

    private static bool CharEq(char a, char b, bool ignoreCase)
        => ignoreCase ? char.ToUpperInvariant(a) == char.ToUpperInvariant(b) : a == b;

    private static bool CharInRange(char c, char lo, char hi, bool ignoreCase)
    {
        if (ignoreCase)
        {
            c = char.ToUpperInvariant(c);
            lo = char.ToUpperInvariant(lo);
            hi = char.ToUpperInvariant(hi);
        }
        return c >= lo && c <= hi;
    }
}
