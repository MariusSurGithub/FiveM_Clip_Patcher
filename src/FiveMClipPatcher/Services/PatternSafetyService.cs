using System.Text.RegularExpressions;

namespace FiveMClipPatcher.Services;

public static class PatternSafetyService
{
    public const int MinWildcardPrefixLength = 4;

    private static readonly HashSet<string> BlockedWildcardPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "cfx",
        "bay",
        "j",
        "as",
        "lv",
        "pd",
        "wk",
        "ap",
    };

    public static bool IsSafePattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        pattern = pattern.Trim();
        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return true;

        if (pattern.StartsWith('*'))
            return pattern.Length >= 5;

        var starIdx = pattern.IndexOf('*');
        if (starIdx <= 0)
            return false;

        var prefix = pattern[..starIdx].TrimEnd('_', '-');
        if (prefix.Length < MinWildcardPrefixLength)
            return false;

        if (BlockedWildcardPrefixes.Contains(prefix))
            return false;

        return true;
    }

    public static IReadOnlyList<string> GetUnsafePatterns(IEnumerable<string> patterns) =>
        patterns.Where(p => !IsSafePattern(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static IReadOnlyList<string> FilterSafePatterns(IEnumerable<string> patterns) =>
        patterns.Where(IsSafePattern).ToList();

    public static string RemoveUnsafePatternLines(string patternsText)
    {
        var lines = patternsText.Replace("\r\n", "\n").Split('\n');
        var kept = lines.Where(line =>
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                return true;

            return IsSafePattern(trimmed);
        });

        return string.Join(Environment.NewLine, kept).TrimEnd();
    }

    internal static bool IsValidResourcePrefixForWildcard(string prefix) =>
        prefix.Length >= MinWildcardPrefixLength && !BlockedWildcardPrefixes.Contains(prefix);
}
