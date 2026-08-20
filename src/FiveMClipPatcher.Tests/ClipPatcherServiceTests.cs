using System.IO;
using System.Text;
using FiveMClipPatcher.Models;
using FiveMClipPatcher.Services;
using FiveMClipPatcher.ViewModels;
using Xunit;

namespace FiveMClipPatcher.Tests;

public class ClipPatcherServiceTests
{
    [Fact]
    public void Exact_finds_substring_between_binary_junk()
    {
        var data = Concat(new byte[] { 0x00, 0xFF }, "modname", new byte[] { 0x00, 0xFF });
        var hits = ClipPatcherService.FindMatches(data, ["modname"], caseInsensitive: false);

        var hit = Assert.Single(hits);
        Assert.Equal("modname", hit.MatchedText);
        Assert.Equal(2, hit.Offset);
        Assert.Equal(7, hit.Length);
    }

    [Fact]
    public void Wildcard_matches_isolated_ascii_run_only()
    {
        var isolated = Concat(new byte[] { 0xFF }, "mod_foo", new byte[] { 0x00 });
        var embedded = Concat(new byte[] { 0x00 }, "xxmod_foo", new byte[] { 0x00 });

        var isolatedHits = ClipPatcherService.FindMatches(isolated, ["mod_*"], caseInsensitive: false);
        var embeddedHits = ClipPatcherService.FindMatches(embedded, ["mod_*"], caseInsensitive: false);

        Assert.Single(isolatedHits);
        Assert.Equal("mod_foo", isolatedHits[0].MatchedText);
        Assert.Empty(embeddedHits);
    }

    [Fact]
    public void Exact_still_finds_name_inside_longer_ascii_run()
    {
        var data = Concat(new byte[] { 0x00 }, "C:\\mods\\17mov_foo\\file.ydr", new byte[] { 0x00 });
        var exact = ClipPatcherService.FindMatches(data, ["17mov_foo"], caseInsensitive: false);
        var wild = ClipPatcherService.FindMatches(data, ["17mov_*"], caseInsensitive: false);

        Assert.Single(exact);
        Assert.Equal("17mov_foo", exact[0].MatchedText);
        Assert.Empty(wild);
    }

    [Fact]
    public void Case_insensitive_exact_is_only_lower_and_upper_not_mixed()
    {
        var data = Encoding.ASCII.GetBytes("bAdStRiNg");
        var hits = ClipPatcherService.FindMatches(data, ["badstring"], caseInsensitive: true);
        Assert.Empty(hits);

        var upper = Encoding.ASCII.GetBytes("BADSTRING");
        Assert.Single(ClipPatcherService.FindMatches(upper, ["badstring"], caseInsensitive: true));
    }

    [Fact]
    public void Null_and_placeholder_replacements_keep_same_length()
    {
        const int length = 11;
        var nulls = ClipPatcherService.BuildReplacementBytes(length, PatchMode.Null, "REMOVED");
        var placeholder = ClipPatcherService.BuildReplacementBytes(length, PatchMode.Placeholder, "REMOVED");

        Assert.Equal(length, nulls.Length);
        Assert.All(nulls, b => Assert.Equal(0, b));
        Assert.Equal(length, placeholder.Length);
        Assert.Equal("REMOVEDREMO", Encoding.ASCII.GetString(placeholder));
    }

    [Fact]
    public void Ascii_ignore_drops_non_ascii_instead_of_question_marks()
    {
        var data = Encoding.ASCII.GetBytes("hello");
        var hits = ClipPatcherService.FindMatches(data, ["héllo"], caseInsensitive: false);
        Assert.Empty(hits);

        var placeholder = ClipPatcherService.BuildReplacementBytes(6, PatchMode.Placeholder, "REMOVÉ");
        Assert.Equal(6, placeholder.Length);
        Assert.DoesNotContain((byte)'?', placeholder);
    }

    [Fact]
    public void CollectFiles_skips_single_file_with_wrong_extension()
    {
        var dir = CreateTempDir();
        var txt = Path.Combine(dir, "clip.txt");
        var clip = Path.Combine(dir, "clip.clip");
        File.WriteAllBytes(txt, Encoding.ASCII.GetBytes("modname"));
        File.WriteAllBytes(clip, Encoding.ASCII.GetBytes("modname"));

        var exts = ClipPatcherService.NormalizeExtensions([".clip"]);
        Assert.Empty(ClipPatcherService.CollectFiles(txt, recursive: false, exts));
        Assert.Equal(clip, Assert.Single(ClipPatcherService.CollectFiles(clip, recursive: false, exts)));
    }

    [Fact]
    public void Patch_in_place_keeps_file_size_and_nulls_match()
    {
        var dir = CreateTempDir();
        var clip = Path.Combine(dir, "take.clip");
        var original = Concat(new byte[] { 0x00, 0xFF }, "modname", new byte[] { 0xAA, 0xBB });
        File.WriteAllBytes(clip, original);
        var originalLength = original.Length;

        var result = new ClipPatcherService().Patch(new PatchOptions
        {
            InputPath = clip,
            Patterns = ["modname"],
            BackupBasePath = Path.Combine(dir, "backups"),
            Mode = PatchMode.Null
        }, progress: null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, result.FilesPatched);
        Assert.Equal(1, result.PatternsPatched);

        var patched = File.ReadAllBytes(clip);
        Assert.Equal(originalLength, patched.Length);
        Assert.Equal(original[0], patched[0]);
        Assert.Equal(original[1], patched[1]);
        Assert.Equal(0, patched[2]);
        Assert.Equal(0, patched[8]);
        Assert.Equal(0xAA, patched[9]);
        Assert.Equal(0xBB, patched[10]);
        Assert.True(Directory.Exists(result.RunDirectory));
        Assert.Contains(ClipPatcherService.BackupFolderName, result.RunDirectory);
    }

    [Fact]
    public void DryRun_does_not_modify_file_or_create_backup()
    {
        var dir = CreateTempDir();
        var clip = Path.Combine(dir, "take.clip");
        var original = Concat(new byte[] { 0x00 }, "modname", new byte[] { 0x00 });
        File.WriteAllBytes(clip, original);

        var result = new ClipPatcherService().Patch(new PatchOptions
        {
            InputPath = clip,
            Patterns = ["modname"],
            BackupBasePath = Path.Combine(dir, "backups"),
            DryRun = true
        }, progress: null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, result.FilesPatched);
        Assert.Null(result.RunDirectory);
        Assert.Equal(original, File.ReadAllBytes(clip));
        Assert.False(Directory.Exists(Path.Combine(dir, "backups", ClipPatcherService.BackupFolderName)));
    }

    [Fact]
    public void ResolveRunDirectory_nests_logs_folder_unless_already_there()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "clip-base-" + Guid.NewGuid().ToString("N"));
        var nested = ClipPatcherService.ResolveRunDirectory(baseDir, "2026-01-01_000000");
        Assert.EndsWith(Path.Combine(ClipPatcherService.BackupFolderName, "run_2026-01-01_000000"), nested);

        var already = ClipPatcherService.ResolveRunDirectory(
            Path.Combine(baseDir, ClipPatcherService.BackupFolderName),
            "2026-01-01_000000");
        Assert.Equal(nested, already);
    }

    [Fact]
    public void ParsePatterns_skips_comments_and_blank_lines()
    {
        const string text = """
            # comment
            17mov_*

            scully_emotemenu
            """;

        var patterns = MainViewModel.ParsePatterns(text);
        Assert.Equal(["17mov_*", "scully_emotemenu"], patterns);
    }

    [Fact]
    public void Integration_real_gta_clips_folder_if_present()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Rockstar Games", "GTA V", "videos", "clips");

        if (!Directory.Exists(dir))
            return;

        var entries = new ClipCatalogService().ListClips(dir, recursive: false, [".clip"]);
        Assert.NotEmpty(entries);

        var withThumb = entries.Where(e => e.ThumbnailPath is not null).ToList();
        Assert.True(withThumb.Count >= entries.Count - 5,
            $"{withThumb.Count}/{entries.Count} clips ont un .jpg sidecar.");

        var sample = withThumb[0];
        Assert.True(File.Exists(sample.ThumbnailPath!));

        var thumb = new ClipThumbnailService().ExtractThumbnailBytes(sample.FilePath);
        Assert.NotNull(thumb);
        Assert.True(thumb!.Length > 512);
    }

    [Fact]
    public void Patch_wrong_extension_single_file_fails_without_touching_it()
    {
        var dir = CreateTempDir();
        var txt = Path.Combine(dir, "nope.txt");
        var original = Encoding.ASCII.GetBytes("modname");
        File.WriteAllBytes(txt, original);

        var result = new ClipPatcherService().Patch(new PatchOptions
        {
            InputPath = txt,
            Patterns = ["modname"],
            BackupBasePath = Path.Combine(dir, "backups"),
            Extensions = [".clip"]
        }, progress: null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(original, File.ReadAllBytes(txt));
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
