using System.IO;

namespace FiveMClipPatcher.Services;

public sealed record ClipFileEntry(
    string FilePath,
    string DisplayName,
    DateTime ModifiedUtc,
    long SizeBytes,
    string? EmbeddedTitle,
    DateTime? EmbeddedRecordedAt,
    string? ThumbnailPath);

public sealed class ClipCatalogService
{
    private const int HeaderScanBytes = 4096;

    public IReadOnlyList<ClipFileEntry> ListClips(string inputPath, bool recursive, IReadOnlyList<string> extensions)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
            return [];

        inputPath = Path.GetFullPath(inputPath);
        var extSet = ClipPatcherService.NormalizeExtensions(extensions);
        var paths = ClipPatcherService.CollectFiles(inputPath, recursive, extSet);

        return paths
            .Select(BuildEntry)
            .OrderByDescending(e => e.ModifiedUtc)
            .ToList();
    }

    private static ClipFileEntry BuildEntry(string filePath)
    {
        var info = new FileInfo(filePath);
        var (embeddedTitle, embeddedDate) = TryReadHeaderMetadata(filePath);

        var displayName = !string.IsNullOrWhiteSpace(embeddedTitle)
            ? embeddedTitle
            : Path.GetFileNameWithoutExtension(filePath);

        var thumbnailPath = ClipThumbnailService.GetSidecarThumbnailPath(filePath);

        return new ClipFileEntry(
            filePath,
            displayName,
            info.LastWriteTimeUtc,
            info.Length,
            embeddedTitle,
            embeddedDate,
            thumbnailPath);
    }

    internal static (string? Title, DateTime? RecordedAt) TryReadHeaderMetadata(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var len = (int)Math.Min(HeaderScanBytes, fs.Length);
            if (len <= 0)
                return (null, null);

            var header = new byte[len];
            _ = fs.Read(header, 0, len);
            var text = System.Text.Encoding.ASCII.GetString(header);

            DateTime? recorded = ClipMetadataParser.TryParseRockstarTimestamp(text);
            var title = ClipMetadataParser.TryParseClipTitle(text, Path.GetFileNameWithoutExtension(filePath));
            return (title, recorded);
        }
        catch
        {
            return (null, null);
        }
    }
}

internal static class ClipMetadataParser
{
    internal static DateTime? TryParseRockstarTimestamp(string headerText)
    {
        // Ex: "Jan 22 2026.13:43:17"
        var match = System.Text.RegularExpressions.Regex.Match(
            headerText,
            @"\b(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\s+\d{1,2}\s+\d{4}\.\d{2}:\d{2}:\d{2}\b",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        if (!match.Success)
            return null;

        var normalized = match.Value.Replace('.', ' ');
        return DateTime.TryParse(normalized, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal, out var dt)
            ? dt
            : null;
    }

    internal static string? TryParseClipTitle(string headerText, string fallbackName)
    {
        if (headerText.Contains(fallbackName, StringComparison.OrdinalIgnoreCase))
            return fallbackName;

        var seq = System.Text.RegularExpressions.Regex.Match(
            headerText,
            @"\d{1,2}-[A-Za-zÀ-ÿ]+-\d{4}(?:-S[ée]quence-\d+)?",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        return seq.Success ? seq.Value : null;
    }
}
