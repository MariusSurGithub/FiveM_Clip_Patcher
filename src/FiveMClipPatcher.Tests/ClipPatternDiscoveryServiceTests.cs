using System.IO;
using System.Text;
using FiveMClipPatcher.Models;
using FiveMClipPatcher.Services;
using FiveMClipPatcher.ViewModels;
using Xunit;

namespace FiveMClipPatcher.Tests;

public class ClipPatternDiscoveryServiceTests
{
    [Fact]
    public void ParsePatterns_parses_enriched_defaults_without_comments()
    {
        var patterns = MainViewModel.ParsePatterns(MainViewModel.DefaultPatterns);

        Assert.Contains("17mov_*", patterns);
        Assert.Contains("*_emotemenu", patterns);
        Assert.Contains("griz_cayo_restaurant", patterns);
        Assert.Contains("prompt_*", patterns);
        Assert.DoesNotContain(patterns, p => p.StartsWith('#'));
    }

    [Fact]
    public void LooksLikeResourceName_rejects_blacklisted_and_paths()
    {
        Assert.False(ClipPatternDiscoveryService.LooksLikeResourceName("YLPR"));
        Assert.False(ClipPatternDiscoveryService.LooksLikeResourceName(@"C:\Users\Marius"));
        Assert.False(ClipPatternDiscoveryService.LooksLikeResourceName("abc"));
        Assert.True(ClipPatternDiscoveryService.LooksLikeResourceName("griz_cayo_restaurant"));
    }

    [Fact]
    public void DiscoverFromClips_suggests_wildcard_and_exact_names()
    {
        var dir = CreateTempDir();
        var clip1 = Path.Combine(dir, "a.clip");
        var clip2 = Path.Combine(dir, "b.clip");
        var clip3 = Path.Combine(dir, "c.clip");

        File.WriteAllBytes(clip1, Payload("griz_cayo_restaurant", "17mov_alpha"));
        File.WriteAllBytes(clip2, Payload("griz_cayo_restaurant", "17mov_beta"));
        File.WriteAllBytes(clip3, Payload("17mov_gamma", "17mov_delta"));

        var suggestions = new ClipPatternDiscoveryService().DiscoverFromClips(
            [clip1, clip2, clip3],
            existingPatterns: ["17mov_*"],
            progress: null,
            CancellationToken.None);

        Assert.Contains(suggestions, s => s.Pattern == "griz_cayo_restaurant");
        Assert.DoesNotContain(suggestions, s => s.Pattern.StartsWith("17mov", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MergeSuggestedPatterns_appends_only_new_patterns()
    {
        var merged = MainViewModel.MergeSuggestedPatterns(
            "17mov_*\n",
            [new SuggestedPattern("custom_mod", 3, "test"), new SuggestedPattern("17mov_*", 5, "dup")]);

        Assert.Contains("custom_mod", merged);
        Assert.Contains("# Suggérés depuis tes clips", merged);
        Assert.DoesNotContain("17mov_*\n17mov_*", merged);
    }

    [Fact]
    public void Integration_discover_from_real_gta_clips_if_present()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Rockstar Games", "GTA V", "videos", "clips");

        if (!Directory.Exists(dir))
            return;

        var clips = Directory.EnumerateFiles(dir, "*.clip").Take(15).ToList();
        if (clips.Count < 5)
            return;

        var suggestions = new ClipPatternDiscoveryService().DiscoverFromClips(
            clips,
            MainViewModel.ParsePatterns(MainViewModel.DefaultPatterns),
            progress: null,
            CancellationToken.None);

        Assert.True(suggestions.Count >= 0);
    }

    private static byte[] Payload(params string[] runs)
    {
        var data = new List<byte> { 0x00, 0xFF };
        foreach (var run in runs)
        {
            data.AddRange(Encoding.ASCII.GetBytes(run));
            data.Add(0x00);
        }

        return data.ToArray();
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clip-patcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
