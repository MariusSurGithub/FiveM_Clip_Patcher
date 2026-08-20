using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using FiveMClipPatcher.Services;

namespace FiveMClipPatcher.ViewModels;

public partial class ClipItemViewModel : ObservableObject
{
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private ImageSource? _thumbnail;
    [ObservableProperty] private bool _isThumbnailLoading;
    [ObservableProperty] private int _matchCount = -1;

    public string FilePath { get; }
    public string? ThumbnailPath { get; }
    public string DisplayName { get; }
    public DateTime DisplayDate { get; }
    public long SizeBytes { get; }

    public string DateText => DisplayDate.ToString("g", CultureInfo.CurrentCulture);
    public string SizeText => FormatSize(SizeBytes);

    public string MatchSummary => MatchCount switch
    {
        < 0 => "",
        0 => "Aucun match",
        1 => "1 match",
        _ => $"{MatchCount} matches"
    };

    public ClipItemViewModel(ClipFileEntry entry)
    {
        FilePath = entry.FilePath;
        ThumbnailPath = entry.ThumbnailPath;
        DisplayName = entry.DisplayName;
        DisplayDate = entry.EmbeddedRecordedAt?.ToLocalTime() ?? entry.ModifiedUtc.ToLocalTime();
        SizeBytes = entry.SizeBytes;
    }

    public async Task LoadThumbnailAsync(ClipThumbnailService service, CancellationToken cancellationToken)
    {
        if (Thumbnail is not null || IsThumbnailLoading)
            return;

        IsThumbnailLoading = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(ThumbnailPath) && File.Exists(ThumbnailPath))
            {
                var sidecarBytes = await Task.Run(() => File.ReadAllBytes(ThumbnailPath), cancellationToken);
                await SetThumbnailAsync(await Task.Run(() => DecodeImage(sidecarBytes), cancellationToken));
                return;
            }

            var bytes = await Task.Run(() => service.ExtractThumbnailBytes(FilePath), cancellationToken);
            if (bytes is null || bytes.Length == 0)
            {
                await SetPlaceholderAsync(DisplayName);
                return;
            }

            await SetThumbnailAsync(await Task.Run(() => DecodeImage(bytes), cancellationToken));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            await SetPlaceholderAsync(DisplayName);
        }
        finally
        {
            IsThumbnailLoading = false;
        }
    }

    private Task SetPlaceholderAsync(string displayName) =>
        SetThumbnailAsync(PlaceholderFactory.Create(displayName));

    private Task SetThumbnailAsync(ImageSource image)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            return dispatcher.InvokeAsync(() => Thumbnail = image).Task;

        Thumbnail = image;
        return Task.CompletedTask;
    }

    private static ImageSource DecodeImage(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = ms;
        img.DecodePixelWidth = 320;
        img.EndInit();
        img.Freeze();
        return img;
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["o", "Ko", "Mo", "Go"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes:0} {units[unit]}"
            : $"{size:0.#} {units[unit]}";
    }
}

internal static class PlaceholderFactory
{
    public static ImageSource Create(string displayName)
    {
        const int w = 160;
        const int h = 90;
        var letter = string.IsNullOrWhiteSpace(displayName) ? "?" : displayName.Trim()[0].ToString().ToUpperInvariant();

        var visual = new System.Windows.Controls.Border
        {
            Width = w,
            Height = h,
            Background = new SolidColorBrush(Color.FromRgb(0x18, 0x1C, 0x24)),
            Child = new System.Windows.Controls.TextBlock
            {
                Text = letter,
                FontSize = 28,
                FontWeight = System.Windows.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0xA0, 0xFF)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            }
        };

        visual.Measure(new System.Windows.Size(w, h));
        visual.Arrange(new System.Windows.Rect(0, 0, w, h));

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }
}
