using System.IO;

namespace FiveMClipPatcher.Services;

public sealed class ClipThumbnailService
{
    private const int TailScanBytes = 4 * 1024 * 1024;
    private const int MinJpegBytes = 2048;
    private const int MaxJpegBytes = 512 * 1024;

    public static string? GetSidecarThumbnailPath(string clipPath)
    {
        if (string.IsNullOrWhiteSpace(clipPath))
            return null;

        var jpg = Path.ChangeExtension(clipPath, ".jpg");
        return File.Exists(jpg) ? jpg : null;
    }

    public byte[]? ExtractThumbnailBytes(string filePath)
    {
        try
        {
            var sidecar = GetSidecarThumbnailPath(filePath);
            if (sidecar is not null)
                return File.ReadAllBytes(sidecar);

            return ExtractEmbeddedJpegBytes(filePath);
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? ExtractEmbeddedJpegBytes(string filePath)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists || info.Length == 0)
            return null;

        var tailLen = (int)Math.Min(TailScanBytes, info.Length);
        var tail = new byte[tailLen];
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            fs.Seek(-tailLen, SeekOrigin.End);
            _ = fs.Read(tail, 0, tailLen);
        }

        var best = FindBestJpeg(tail);
        if (best is not null)
            return best;

        var headLen = (int)Math.Min(512 * 1024, info.Length);
        if (headLen == tailLen)
            return null;

        var head = new byte[headLen];
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            _ = fs.Read(head, 0, headLen);

        return FindBestJpeg(head);
    }

    internal static byte[]? FindBestJpeg(ReadOnlySpan<byte> data)
    {
        byte[]? best = null;
        var bestLen = 0;
        var bestOffset = -1;

        for (var i = 0; i < data.Length - 2; i++)
        {
            if (data[i] != 0xFF || data[i + 1] != 0xD8)
                continue;

            if (i + 2 < data.Length && data[i + 2] is not (0xFF or 0xE0 or 0xE1 or 0xDB))
                continue;

            var jpeg = ExtractJpegUntilEoi(data, i);
            if (jpeg is null)
                continue;

            if (jpeg.Length is >= MinJpegBytes and <= MaxJpegBytes)
            {
                if (jpeg.Length > bestLen || (jpeg.Length == bestLen && i > bestOffset))
                {
                    best = jpeg;
                    bestLen = jpeg.Length;
                    bestOffset = i;
                }
            }
        }

        if (best is not null)
            return best;

        return FindAnySmallJpeg(data);
    }

    private static byte[]? FindAnySmallJpeg(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i < data.Length - 2; i++)
        {
            if (data[i] != 0xFF || data[i + 1] != 0xD8)
                continue;

            var jpeg = ExtractJpegUntilEoi(data, i);
            if (jpeg is { Length: >= 512 and <= MaxJpegBytes })
                return jpeg;
        }

        return null;
    }

    private static byte[]? ExtractJpegUntilEoi(ReadOnlySpan<byte> data, int start)
    {
        for (var j = start + 2; j < data.Length; j++)
        {
            if (data[j - 1] == 0xFF && data[j] == 0xD9)
                return data.Slice(start, j - start + 1).ToArray();
        }

        return null;
    }
}
