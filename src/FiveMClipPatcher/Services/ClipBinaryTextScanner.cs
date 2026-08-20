using System.IO;
using System.Text;

namespace FiveMClipPatcher.Services;

internal static class ClipBinaryTextScanner
{
    private const int DefaultChunkSize = 4 * 1024 * 1024;

    internal static IEnumerable<string> ExtractAsciiRuns(byte[] data) =>
        ExtractAsciiRuns(data, 0, data.Length);

    internal static IEnumerable<string> ExtractAsciiRuns(byte[] data, int offset, int count)
    {
        var end = offset + count;
        var current = new StringBuilder();

        for (var i = offset; i < end; i++)
        {
            var b = data[i];
            if (b is >= 32 and <= 126)
            {
                current.Append((char)b);
            }
            else if (current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }
        }

        if (current.Length > 0)
            yield return current.ToString();
    }

    internal static void ScanFile(string filePath, Action<string> onRun, int chunkSize = DefaultChunkSize)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length == 0)
            return;

        var buffer = new byte[Math.Min(chunkSize, (int)Math.Min(fs.Length, chunkSize))];
        var carry = new StringBuilder();

        while (true)
        {
            var read = fs.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;

            for (var i = 0; i < read; i++)
            {
                var b = buffer[i];
                if (b is >= 32 and <= 126)
                {
                    carry.Append((char)b);
                    continue;
                }

                if (carry.Length > 0)
                {
                    onRun(carry.ToString());
                    carry.Clear();
                }
            }
        }

        if (carry.Length > 0)
            onRun(carry.ToString());
    }
}
