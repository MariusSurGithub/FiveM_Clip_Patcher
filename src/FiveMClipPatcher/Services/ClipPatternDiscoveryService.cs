using System.IO;
using System.Text.RegularExpressions;
using FiveMClipPatcher.Models;

namespace FiveMClipPatcher.Services;

public sealed class ClipPatternDiscoveryService
{
    private const int MinRunLength = 6;
    private const int MaxRunLength = 64;
    private const int MinClipCountExact = 3;
    private const int MinVariantsForWildcard = 4;
    private const int MinWildcardPrefixLength = PatternSafetyService.MinWildcardPrefixLength;
    private const int MaxSuggestions = 25;

    private static readonly HashSet<string> Blacklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "YLPR",
        "REMOVED",
        "NULL",
        "TRUE",
        "FALSE",
        "HTTP",
        "HTTPS",
        "LOCALAPPDATA",
        "ROCKSTAR",
        "SEQUENCE",
        "GAMES",
        "VIDEOS",
        "CLIPS",
        "USERS",
        "WINDOWS",
        "PROGRAM",
        "FILES",
        "GTA5",
        "FIVEM",
    };

    private static readonly Regex ResourceLike = new(
        @"^[a-zA-Z0-9][a-zA-Z0-9_-]*[a-zA-Z0-9]$|^[a-zA-Z0-9]{4,}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DateLike = new(
        @"^\d{1,2}-[A-Za-zÀ-ÿ]+-\d{4}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public IReadOnlyList<SuggestedPattern> DiscoverFromClips(
        IReadOnlyList<string> clipPaths,
        IReadOnlyList<string> existingPatterns,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (clipPaths.Count == 0)
            return [];

        var existing = BuildExistingPatternSet(existingPatterns);
        var perClipRuns = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var globalCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < clipPaths.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clipPath = clipPaths[i];
            progress?.Report($"Analyse {i + 1}/{clipPaths.Count} : {Path.GetFileName(clipPath)}…");

            if (!File.Exists(clipPath))
                continue;

            var runsInClip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ClipBinaryTextScanner.ScanFile(clipPath, run =>
            {
                if (!LooksLikeResourceName(run))
                    return;

                runsInClip.Add(run);
            });

            foreach (var run in runsInClip)
            {
                if (IsAlreadyCovered(run, existing))
                    continue;

                globalCounts.TryGetValue(run, out var count);
                globalCounts[run] = count + 1;
            }

            perClipRuns[clipPath] = runsInClip;
        }

        var suggestions = new List<SuggestedPattern>();
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var prefixGroups = globalCounts.Keys
            .Where(k => k.Contains('_'))
            .Select(k => (Name: k, Prefix: GetPrefix(k)))
            .Where(x => x.Prefix.Length >= MinWildcardPrefixLength)
            .GroupBy(x => x.Prefix, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= MinVariantsForWildcard)
            .OrderByDescending(g => g.Sum(x => globalCounts[x.Name]))
            .ToList();

        foreach (var group in prefixGroups)
        {
            if (!PatternSafetyService.IsValidResourcePrefixForWildcard(group.Key))
                continue;

            var wildcard = $"{group.Key}_*";
            if (existing.Contains(NormalizePatternKey(wildcard)) || consumed.Contains(wildcard))
                continue;

            if (!PatternSafetyService.IsSafePattern(wildcard))
                continue;

            var clipCount = CountClipsWithPrefix(perClipRuns, group.Key);
            if (clipCount < MinClipCountExact)
                continue;

            suggestions.Add(new SuggestedPattern(wildcard, clipCount, $"{group.Count()} variantes"));
            consumed.Add(wildcard);

            foreach (var entry in group)
                consumed.Add(entry.Name);
        }

        foreach (var (name, clipCount) in globalCounts.OrderByDescending(kv => kv.Value))
        {
            if (clipCount < MinClipCountExact || consumed.Contains(name))
                continue;

            if (IsAlreadyCovered(name, existing))
                continue;

            suggestions.Add(new SuggestedPattern(name, clipCount, "présent dans plusieurs clips"));
            consumed.Add(name);
        }

        return suggestions
            .Where(s => PatternSafetyService.IsSafePattern(s.Pattern))
            .OrderByDescending(s => s.ClipCount)
            .ThenBy(s => s.Pattern, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSuggestions)
            .ToList();
    }

    internal static bool LooksLikeResourceName(string run)
    {
        if (run.Length is < MinRunLength or > MaxRunLength)
            return false;

        if (!run.Contains('_') && !run.Contains('-'))
            return false;

        if (Blacklist.Contains(run))
            return false;

        if (DateLike.IsMatch(run))
            return false;

        if (run.Contains('\\') || run.Contains('/') || run.Contains(':'))
            return false;

        if (run.StartsWith('.') || run.EndsWith('.'))
            return false;

        if (run.EndsWith(".ytyp", StringComparison.OrdinalIgnoreCase)
            || run.EndsWith(".ydr", StringComparison.OrdinalIgnoreCase)
            || run.EndsWith(".ytd", StringComparison.OrdinalIgnoreCase)
            || run.EndsWith(".ymap", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!ResourceLike.IsMatch(run))
            return false;

        if (run.Contains('_'))
        {
            var prefix = run[..run.IndexOf('_')];
            if (prefix.Length < MinWildcardPrefixLength)
                return false;
        }
        else if (run.Contains('-') && run.Length < 10)
        {
            return false;
        }

        return run.Any(char.IsLetter);
    }

    private static string GetPrefix(string name)
    {
        var idx = name.IndexOf('_');
        if (idx <= 0)
            return string.Empty;

        return name[..idx];
    }
    private static int CountClipsWithPrefix(Dictionary<string, HashSet<string>> perClipRuns, string prefix)
    {
        var count = 0;
        foreach (var runs in perClipRuns.Values)
        {
            if (runs.Any(r => r.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase)))
                count++;
        }

        return count;
    }

    private static HashSet<string> BuildExistingPatternSet(IReadOnlyList<string> patterns)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                continue;

            set.Add(NormalizePatternKey(pattern.Trim()));
        }

        return set;
    }

    private static bool IsAlreadyCovered(string run, HashSet<string> existing)
    {
        foreach (var pattern in existing)
        {
            if (string.Equals(pattern, run, StringComparison.OrdinalIgnoreCase))
                return true;

            if (pattern.Contains('*') || pattern.Contains('?'))
            {
                if (WildcardCovers(pattern, run))
                    return true;
            }
            else if (run.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool WildcardCovers(string pattern, string run)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "\\z";
        return Regex.IsMatch(run, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string NormalizePatternKey(string pattern) => pattern.Trim();
}
