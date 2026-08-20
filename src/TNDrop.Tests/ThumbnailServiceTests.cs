using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TNDrop.Services;

public class ThumbnailServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tndrop-test-" + Guid.NewGuid());

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // Low-level pixel-buffer constructor: deterministic exact dimensions, no Dispatcher/visual
    // tree required (unlike rendering a DrawingVisual), so it works whether or not the test
    // happens to run on an STA thread.
    private static BitmapSource MakeBitmap(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
    }

    [StaFact]
    public void SaveImage_creates_320px_wide_thumbnail_preserving_aspect()
    {
        var service = new ThumbnailService(_dir);
        var (_, thumbFile) = service.SaveImage(MakeBitmap(640, 480));

        var thumb = Assert.IsAssignableFrom<BitmapSource>(service.LoadThumb(thumbFile));
        Assert.Equal(320, thumb.PixelWidth);
        Assert.Equal(240, thumb.PixelHeight);
    }

    [StaFact]
    public void SaveImage_does_not_upscale_images_already_narrower_than_320px()
    {
        var service = new ThumbnailService(_dir);
        var (_, thumbFile) = service.SaveImage(MakeBitmap(100, 80));

        var thumb = Assert.IsAssignableFrom<BitmapSource>(service.LoadThumb(thumbFile));
        Assert.Equal(100, thumb.PixelWidth);
        Assert.Equal(80, thumb.PixelHeight);
    }

    [StaFact]
    public void LoadThumb_returns_null_for_missing_file_without_throwing()
    {
        var service = new ThumbnailService(_dir);
        Assert.Null(service.LoadThumb("does-not-exist.png"));
    }

    [StaFact]
    public void LoadThumb_returns_the_same_cached_instance_on_repeated_calls()
    {
        var service = new ThumbnailService(_dir);
        var (_, thumbFile) = service.SaveImage(MakeBitmap(640, 480));

        var first = service.LoadThumb(thumbFile);
        var second = service.LoadThumb(thumbFile);

        Assert.NotNull(first);
        Assert.Same(first, second);
    }
}
