using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.RegularExpressions;
using FiveMClipPatcher.Models;

namespace FiveMClipPatcher.Services;

public sealed class ClipPatcherService
{
    public const string BackupFolderName = "clip_patcher_files_backups_logs";

    private static readonly Encoding AsciiIgnore = Encoding.GetEncoding(
        "us-ascii",
        new EncoderReplacementFallback(string.Empty),
        new DecoderReplacementFallback(string.Empty));

    public static string GetDefaultGtaClipsPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            throw new InvalidOperationException("LOCALAPPDATA introuvable.");

        return Path.Combine(localAppData, "Rockstar Games", "GTA V", "videos", "clips");
    }

    public static string GetDefaultBackupBasePath() =>
        Path.Combine(AppContext.BaseDirectory, BackupFolderName);

    public static string ResolveRunDirectory(string backupBasePath, string runTimestamp)
    {
        var full = Path.GetFullPath(string.IsNullOrWhiteSpace(backupBasePath)
            ? GetDefaultBackupBasePath()
            : backupBasePath);

        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetFileName(trimmed), BackupFolderName, StringComparison.OrdinalIgnoreCase))
            full = Path.Combine(trimmed, BackupFolderName);

        return Path.Combine(full, $"run_{runTimestamp}");
    }

    public PatchRunResult Patch(PatchOptions options, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (options.Patterns.Count == 0)
            return Fail("Aucun pattern. Ajoute des noms de mods (un par ligne, wildcards * et ? OK).");

        if (string.IsNullOrWhiteSpace(options.InputPath))
            return Fail("Choisis un dossier ou un fichier .clip.");

        var inputPath = Path.GetFullPath(options.InputPath);
        if (!File.Exists(inputPath) && !Directory.Exists(inputPath))
            return Fail($"Chemin introuvable : {inputPath}");

        var extensions = NormalizeExtensions(options.Extensions);
        var files = ResolveFiles(inputPath, options, extensions);
        if (files.Count == 0)
            return Fail(options.SelectedFiles is { Count: > 0 }
                ? "Aucun fichier sélectionné valide."
                : $"Aucun fichier {string.Join(", ", extensions)} trouvé.");

        var runTs = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string? runDir = null;
        StreamWriter? log = null;

        if (!options.DryRun)
        {
            runDir = ResolveRunDirectory(options.BackupBasePath, runTs);
            Directory.CreateDirectory(runDir);
            log = new StreamWriter(Path.Combine(runDir, "patchlog.txt"), false, Encoding.UTF8);
        }

        using (log)
        {
            var verb = options.DryRun ? "Scan" : "Traitement";
            progress?.Report($"{verb} de {files.Count} fichier(s)…");
            progress?.Report($"Mode : {options.Mode}, casse : {(options.CaseInsensitive ? "ignorée" : "respectée")}");
            if (options.DryRun)
                progress?.Report("Dry-run : aucun fichier modifié.");
            progress?.Report("");

            log?.WriteLine($"Clip Patcher Run: {runTs}");
            log?.WriteLine($"Input: {inputPath}");
            log?.WriteLine($"Patterns: {string.Join(", ", options.Patterns)}");
            log?.WriteLine($"Mode: {options.Mode}");
            log?.WriteLine($"Case insensitive: {options.CaseInsensitive}");
            log?.WriteLine($"Extensions: {string.Join(", ", extensions)}");
            log?.WriteLine(new string('-', 50));
            log?.WriteLine();

            var hits = new List<PatchHit>();
            var filesPatched = 0;
            var patternsPatched = 0;
            var fileIndex = 0;

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                fileIndex++;
                progress?.Report($"Fichier {fileIndex}/{files.Count} : {Path.GetFileName(file)}…");

                try
                {
                    var fileHits = ProcessFile(file, options, runDir, log);
                    if (fileHits.Count > 0)
                    {
                        filesPatched++;
                        patternsPatched += fileHits.Count;
                        hits.AddRange(fileHits);
                        progress?.Report($"  → {fileHits.Count} pattern(s) trouvé(s)");
                    }
                    else
                    {
                        progress?.Report("  → aucun match");
                    }
                }
                catch (Exception ex)
                {
                    progress?.Report($"  → erreur : {ex.Message}");
                    log?.WriteLine($"{Path.GetFileName(file)}: ERROR {ex.Message}");
                }
            }

            log?.WriteLine();
            log?.WriteLine("Summary:");
            log?.WriteLine($"Files processed: {files.Count}");
            log?.WriteLine($"Files patched: {filesPatched}");
            log?.WriteLine($"Total patterns patched: {patternsPatched}");

            progress?.Report("");
            progress?.Report(options.DryRun ? "Scan terminé." : "Terminé.");
            progress?.Report($"Fichiers traités : {files.Count}");
            progress?.Report($"Fichiers avec match : {filesPatched}");
            progress?.Report($"Patterns : {patternsPatched}");
            if (runDir is not null)
                progress?.Report($"Backups + log : {runDir}");

            return new PatchRunResult
            {
                Success = true,
                FilesProcessed = files.Count,
                FilesPatched = filesPatched,
                PatternsPatched = patternsPatched,
                RunDirectory = runDir,
                Hits = hits
            };
        }
    }

    public static IReadOnlyList<PatchHit> FindMatches(byte[] data, IReadOnlyList<string> patterns, bool caseInsensitive)
    {
        return FindAllMatches(data, patterns, caseInsensitive)
            .Select(m => new PatchHit
            {
                FileName = "",
                FilePath = "",
                MatchedText = m.Text,
                Pattern = m.Pattern,
                Offset = m.Start,
                Length = m.Bytes.Length
            })
            .ToList();
    }

    public static byte[] BuildReplacementBytes(int length, PatchMode mode, string placeholder)
    {
        return BuildReplacement(length, mode, placeholder);
    }

    private static List<PatchHit> ProcessFile(string filePath, PatchOptions options, string? backupDir, StreamWriter? log)
    {
        var data = File.ReadAllBytes(filePath);
        var matches = FindAllMatches(data, options.Patterns, options.CaseInsensitive);
        if (matches.Count == 0)
            return [];

        matches.Sort((a, b) => b.Start.CompareTo(a.Start));

        var hits = new List<PatchHit>(matches.Count);
        foreach (var match in matches)
        {
            hits.Add(new PatchHit
            {
                FileName = Path.GetFileName(filePath),
                FilePath = filePath,
                MatchedText = match.Text,
                Pattern = match.Pattern,
                Offset = match.Start,
                Length = match.Bytes.Length
            });

            var action = options.DryRun ? "match" : "patched";
            log?.WriteLine(
                $"{Path.GetFileName(filePath)}: {action} '{match.Text}' (pattern: '{match.Pattern}') " +
                $"at offset {match.Start} (len={match.Bytes.Length})");
        }

        if (options.DryRun)
            return hits;

        if (backupDir is not null)
        {
            var destBackup = UniquePath(Path.Combine(backupDir, Path.GetFileName(filePath)));
            File.Copy(filePath, destBackup, overwrite: false);
        }

        ApplyInPlace(filePath, matches, options);
        return hits;
    }

    private static void ApplyInPlace(string filePath, List<MatchInfo> matches, PatchOptions options)
    {
        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length == 0)
            return;

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var originalLength = fs.Length;

        using var mmf = MemoryMappedFile.CreateFromFile(
            fs,
            mapName: null,
            capacity: originalLength,
            access: MemoryMappedFileAccess.ReadWrite,
            inheritability: HandleInheritability.None,
            leaveOpen: true);

        using (var accessor = mmf.CreateViewAccessor(0, originalLength, MemoryMappedFileAccess.ReadWrite))
        {
            foreach (var match in matches)
            {
                var replacement = BuildReplacement(match.Bytes.Length, options.Mode, options.Placeholder);
                accessor.WriteArray(match.Start, replacement, 0, replacement.Length);
            }

            accessor.Flush();
        }

        fs.Flush(true);

        if (fs.Length != originalLength)
            throw new IOException($"La taille du fichier a changé ({originalLength} → {fs.Length}). Patch aborté.");
    }

    private static List<MatchInfo> FindAllMatches(byte[] data, IReadOnlyList<string> patterns, bool caseInsensitive)
    {
        var all = new List<MatchInfo>();
        foreach (var pattern in patterns)
        {
            var matches = IsWildcard(pattern)
                ? FindWildcardMatches(data, pattern, caseInsensitive)
                : FindExactMatches(data, pattern, caseInsensitive);
            all.AddRange(matches);
        }

        return all
            .GroupBy(m => (m.Start, m.Bytes.Length))
            .Select(g => g.First())
            .ToList();
    }

    private static List<MatchInfo> FindWildcardMatches(byte[] data, string pattern, bool caseInsensitive)
    {
        var matches = new List<MatchInfo>();
        var regex = WildcardToRegex(pattern, caseInsensitive);
        if (regex is null)
            return matches;

        foreach (var (start, ascii) in ExtractAsciiStrings(data))
        {
            if (!regex.IsMatch(ascii))
                continue;

            matches.Add(new MatchInfo(start, ascii, AsciiIgnore.GetBytes(ascii), pattern));
        }

        return matches;
    }

    private static List<MatchInfo> FindExactMatches(byte[] data, string pattern, bool caseInsensitive)
    {
        var matches = new List<MatchInfo>();
        var candidates = new HashSet<string>(StringComparer.Ordinal) { pattern };
        if (caseInsensitive)
        {
            candidates.Add(pattern.ToLowerInvariant());
            candidates.Add(pattern.ToUpperInvariant());
        }

        foreach (var candidate in candidates)
        {
            var needle = AsciiIgnore.GetBytes(candidate);
            if (needle.Length == 0)
                continue;

            var start = 0;
            while (true)
            {
                var idx = IndexOf(data, needle, start);
                if (idx < 0)
                    break;

                matches.Add(new MatchInfo(idx, candidate, needle, pattern));
                start = idx + 1;
            }
        }

        return matches;
    }

    private static IEnumerable<(int Start, string Text)> ExtractAsciiStrings(byte[] data)
    {
        var current = new StringBuilder();
        var currentStart = 0;

        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            if (b is >= 32 and <= 126)
            {
                if (current.Length == 0)
                    currentStart = i;
                current.Append((char)b);
            }
            else if (current.Length > 0)
            {
                yield return (currentStart, current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
            yield return (currentStart, current.ToString());
    }

    private static byte[] BuildReplacement(int length, PatchMode mode, string placeholder)
    {
        if (mode == PatchMode.Null || string.IsNullOrEmpty(placeholder))
            return new byte[length];

        var repeated = new StringBuilder(length + placeholder.Length);
        while (repeated.Length < length)
            repeated.Append(placeholder);

        var sliced = repeated.ToString(0, length);
        var encoded = AsciiIgnore.GetBytes(sliced);
        if (encoded.Length == length)
            return encoded;

        var repl = new byte[length];
        Array.Copy(encoded, repl, Math.Min(encoded.Length, length));
        return repl;
    }

    private static Regex? WildcardToRegex(string pattern, bool caseInsensitive)
    {
        try
        {
            var sb = new StringBuilder("^");
            foreach (var ch in pattern)
            {
                sb.Append(ch switch
                {
                    '*' => ".*",
                    '?' => ".",
                    _ => Regex.Escape(ch.ToString())
                });
            }

            sb.Append(@"\z");
            var options = RegexOptions.CultureInvariant | RegexOptions.Compiled;
            if (caseInsensitive)
                options |= RegexOptions.IgnoreCase;

            return new Regex(sb.ToString(), options);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        if (start >= haystack.Length)
            return -1;

        var idx = haystack.AsSpan(start).IndexOf(needle);
        return idx < 0 ? -1 : start + idx;
    }

    private static List<string> ResolveFiles(string inputPath, PatchOptions options, HashSet<string> extensions)
    {
        if (options.SelectedFiles is { Count: > 0 })
        {
            return options.SelectedFiles
                .Select(Path.GetFullPath)
                .Where(f => File.Exists(f) && extensions.Contains(Path.GetExtension(f)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return CollectFiles(inputPath, options.Recursive, extensions);
    }

    internal static List<string> CollectFiles(string inputPath, bool recursive, HashSet<string> extensions)
    {
        if (File.Exists(inputPath))
        {
            return extensions.Contains(Path.GetExtension(inputPath))
                ? [inputPath]
                : [];
        }

        var search = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateFiles(inputPath, "*", search)
            .Where(f => extensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static HashSet<string> NormalizeExtensions(IReadOnlyList<string> extensions)
    {
        var set = extensions
            .Select(NormalizeExtension)
            .Where(e => e.Length > 1)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (set.Count == 0)
            set.Add(".clip");

        return set;
    }

    private static string NormalizeExtension(string ext)
    {
        ext = ext.Trim();
        if (string.IsNullOrEmpty(ext))
            return "";
        return ext.StartsWith('.') ? ext.ToLowerInvariant() : "." + ext.ToLowerInvariant();
    }

    private static bool IsWildcard(string pattern) => pattern.Contains('*') || pattern.Contains('?');

    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
            return path;

        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var i = 2;
        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{name}_{i}{ext}");
            i++;
        } while (File.Exists(candidate));

        return candidate;
    }

    private static PatchRunResult Fail(string error) => new() { Success = false, Error = error };

    private readonly record struct MatchInfo(int Start, string Text, byte[] Bytes, string Pattern);
}
