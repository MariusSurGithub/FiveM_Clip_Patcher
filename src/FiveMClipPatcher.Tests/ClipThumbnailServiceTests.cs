using System.IO;
using System.Text;
using FiveMClipPatcher.Models;
using FiveMClipPatcher.Services;
using Xunit;

namespace FiveMClipPatcher.Tests;

public class ClipThumbnailServiceTests
{
    [Fact]
    public void FindBestJpeg_picks_largest_plausible_image()
    {
        var small = MinimalJpeg(2500);
        var large = MinimalJpeg(12000);
        var data = Encoding.ASCII.GetBytes("YLPRjunk")
            .Concat(new byte[1000])
            .Concat(small)
            .Concat(large)
            .ToArray();

        var best = ClipThumbnailService.FindBestJpeg(data);
        Assert.NotNull(best);
        Assert.Equal(large.Length, best!.Length);
    }

    [Fact]
    public void ExtractThumbnailBytes_reads_jpeg_from_file_tail()
    {
        var dir = CreateTempDir();
        var clip = Path.Combine(dir, "test.clip");
        var jpeg = MinimalJpeg(12000);
        var payload = Encoding.ASCII.GetBytes("YLPR").Concat(new byte[8000]).Concat(jpeg).ToArray();
        File.WriteAllBytes(clip, payload);

        var bytes = new ClipThumbnailService().ExtractThumbnailBytes(clip);
        Assert.NotNull(bytes);
        Assert.Equal(jpeg.Length, bytes!.Length);
    }

    [Fact]
    public void ExtractThumbnailBytes_prefers_sidecar_jpg()
    {
        var dir = CreateTempDir();
        var clip = Path.Combine(dir, "19-Aout-2026-Sequence-0005.clip");
        var jpg = Path.Combine(dir, "19-Aout-2026-Sequence-0005.jpg");
        var embedded = MinimalJpeg(12000);
        File.WriteAllBytes(clip, embedded);
        var sidecar = MinimalJpeg(800);
        File.WriteAllBytes(jpg, sidecar);

        var bytes = new ClipThumbnailService().ExtractThumbnailBytes(clip);
        Assert.NotNull(bytes);
        Assert.Equal(sidecar.Length, bytes!.Length);
    }

    [Fact]
    public void GetSidecarThumbnailPath_finds_companion_jpg()
    {
        var dir = CreateTempDir();
        var clip = Path.Combine(dir, "test.clip");
        var jpg = Path.ChangeExtension(clip, ".jpg");
        File.WriteAllBytes(clip, [0x01]);
        File.WriteAllBytes(jpg, MinimalJpeg(900));

        var path = ClipThumbnailService.GetSidecarThumbnailPath(clip);
        Assert.Equal(jpg, path);
    }

    [Fact]
    public void Catalog_includes_thumbnail_path_when_jpg_exists()
    {
        var dir = CreateTempDir();
        var clip = Path.Combine(dir, "15-Juin-2026-Sequence-0001.clip");
        var jpg = Path.ChangeExtension(clip, ".jpg");
        File.WriteAllBytes(clip, Encoding.ASCII.GetBytes("YLPRtest"));
        File.WriteAllBytes(jpg, MinimalJpeg(900));

        var entry = Assert.Single(new ClipCatalogService().ListClips(dir, recursive: false, [".clip"]));
        Assert.Equal(jpg, entry.ThumbnailPath);
    }

    [Fact]
    public void Patch_uses_selected_files_only()
    {
        var dir = CreateTempDir();
        var clip1 = Path.Combine(dir, "a.clip");
        var clip2 = Path.Combine(dir, "b.clip");
        File.WriteAllBytes(clip1, Concat(new byte[] { 0x00 }, "modname", new byte[] { 0x00 }));
        File.WriteAllBytes(clip2, Concat(new byte[] { 0x00 }, "modname", new byte[] { 0x00 }));

        var result = new ClipPatcherService().Patch(new PatchOptions
        {
            InputPath = dir,
            SelectedFiles = [clip1],
            Patterns = ["modname"],
            BackupBasePath = Path.Combine(dir, "backups"),
            DryRun = true
        }, progress: null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, result.FilesProcessed);
    }

    private static byte[] MinimalJpeg(int contentLength)
    {
        var jpeg = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        jpeg.AddRange(Enumerable.Repeat((byte)0x41, Math.Max(0, contentLength - 6)));
        jpeg.Add(0xFF);
        jpeg.Add(0xD9);
        return jpeg.ToArray();
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clip-patcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static byte[] Concat(byte[] left, string ascii, byte[] right)
    {
        var mid = Encoding.ASCII.GetBytes(ascii);
        var data = new byte[left.Length + mid.Length + right.Length];
        Buffer.BlockCopy(left, 0, data, 0, left.Length);
        Buffer.BlockCopy(mid, 0, data, left.Length, mid.Length);
        Buffer.BlockCopy(right, 0, data, left.Length + mid.Length, right.Length);
        return data;
    }
}
