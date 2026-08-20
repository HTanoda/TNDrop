using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TNDrop.Core;

namespace TNDrop.Services;

/// <summary>
/// Saves clipboard images into an ItemStore's blobs directory as PNG, and lazily reloads
/// thumbnails from disk for display. One instance is shared for the lifetime of the shelf; it
/// does not track which items it created, so callers own persisting the returned file names onto
/// the ClipItem.
/// </summary>
public sealed class ThumbnailService
{
    private const string Module = "ThumbnailService";
    private const int ThumbnailMaxWidth = 320;

    // ShelfViewModel rebuilds Cards/PinnedCards (and therefore every CardViewModel) from scratch
    // on every ItemStore.Changed, so without this cache the same on-disk thumbnail gets
    // re-decoded on every background clipboard event even though the file itself never changed.
    // Keyed by thumb file name; capped with simple least-recently-used eviction so a long-running
    // shelf doesn't grow this unbounded.
    private const int ThumbCacheCapacity = 64;

    private readonly string _blobsDir;
    private readonly HashSet<string> _warnedMissing = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _warnLock = new();

    // Shared LruCache rather than a private copy: the hand-rolled version here paired an
    // OrdinalIgnoreCase dictionary with a LinkedList<string> whose Remove is ordinal, so a hit
    // under different casing would orphan an order node and let eviction drop live entries. That
    // was unreachable in practice -- these keys are GUID file names this class generates itself,
    // always the same casing -- but the identical bug was live in ShellImaging, so both now use
    // the one node-based implementation that cannot desync. See LruCache's remarks.
    private readonly LruCache<ImageSource> _thumbCache =
        new(ThumbCacheCapacity, StringComparer.OrdinalIgnoreCase);

    public ThumbnailService(string blobsDir)
    {
        if (string.IsNullOrWhiteSpace(blobsDir))
        {
            throw new ArgumentException("blobsDir must not be empty.", nameof(blobsDir));
        }

        _blobsDir = blobsDir;

        try
        {
            Directory.CreateDirectory(_blobsDir);
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Warn(Module, $"Failed to create blobs directory: {ex.Message}");
        }
    }

    /// <summary>
    /// Saves <paramref name="image"/> as a full-size PNG blob and a width-320px thumbnail PNG
    /// (aspect preserved; images already narrower than 320px are not upscaled -- the thumbnail
    /// is just a copy of the full image in that case). Returns the two blob file names (not full
    /// paths -- ClipItem.ImageFile/ThumbFile store names, matching BlobsDir-relative lookup).
    /// Encodes the full-size PNG itself; callers that already have PNG-encoded bytes (e.g. because
    /// they needed them to compute a content hash before deciding whether to save at all) should
    /// use the <see cref="SaveImage(byte[], BitmapSource)"/> overload instead, to avoid encoding
    /// the same full-size image twice.
    /// </summary>
    public (string imageFile, string thumbFile) SaveImage(BitmapSource image)
    {
        if (image is null)
        {
            throw new ArgumentNullException(nameof(image));
        }

        var frozenSource = Freeze(image);
        var pngBytes = EncodePng(frozenSource);
        return SaveImage(pngBytes, frozenSource);
    }

    /// <summary>
    /// Saves already-PNG-encoded <paramref name="pngBytes"/> as the full-size blob verbatim (no
    /// re-encode) and builds/saves a width-320px thumbnail from <paramref name="sourceForThumb"/>
    /// (which must be the same image <paramref name="pngBytes"/> was encoded from -- this method
    /// has no way to verify that). Exists so a caller who already encoded the image once (e.g. to
    /// hash it) doesn't pay a second full-size PNG encode here; see
    /// <see cref="SaveImage(BitmapSource)"/> for the single-argument convenience overload that
    /// does the encode itself.
    /// </summary>
    public (string imageFile, string thumbFile) SaveImage(byte[] pngBytes, BitmapSource sourceForThumb)
    {
        if (pngBytes is null)
        {
            throw new ArgumentNullException(nameof(pngBytes));
        }

        if (sourceForThumb is null)
        {
            throw new ArgumentNullException(nameof(sourceForThumb));
        }

        var frozenSource = Freeze(sourceForThumb);

        var id = Guid.NewGuid().ToString("N");
        var imageFile = $"{id}.png";
        var thumbFile = $"{id}_thumb.png";

        File.WriteAllBytes(Path.Combine(_blobsDir, imageFile), pngBytes);

        var thumbnail = BuildThumbnail(frozenSource, ThumbnailMaxWidth);
        SavePng(thumbnail, Path.Combine(_blobsDir, thumbFile));

        return (imageFile, thumbFile);
    }

    /// <summary>
    /// Loads a thumbnail blob by file name. Returns null (never throws) when the name is empty
    /// or the file is missing/unreadable; a missing file is logged as a Warn exactly once per
    /// file name for the lifetime of this instance, so a card that re-renders repeatedly (e.g.
    /// during scrolling) doesn't flood the log.
    /// </summary>
    public ImageSource? LoadThumb(string? thumbFile)
    {
        if (string.IsNullOrEmpty(thumbFile))
        {
            return null;
        }

        if (_thumbCache.TryGet(thumbFile, out var cached))
        {
            return cached;
        }

        var path = Path.Combine(_blobsDir, thumbFile);

        try
        {
            if (!File.Exists(path))
            {
                WarnMissingOnce(thumbFile);
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            using var stream = new MemoryStream(bytes);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            _thumbCache.Set(thumbFile, bitmap);
            return bitmap;
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Warn(Module, $"Failed to load thumbnail '{thumbFile}': {ex.Message}");
            return null;
        }
    }

    private void WarnMissingOnce(string thumbFile)
    {
        lock (_warnLock)
        {
            if (!_warnedMissing.Add(thumbFile))
            {
                return;
            }
        }

        FileLogger.Instance?.Warn(Module, $"Thumbnail file not found: {thumbFile}");
    }

    private static BitmapSource BuildThumbnail(BitmapSource source, int maxWidth)
    {
        if (source.PixelWidth <= maxWidth)
        {
            // Already narrower than (or equal to) the target width: don't upscale, just reuse
            // the full-size bitmap as the thumbnail.
            return source;
        }

        var scale = (double)maxWidth / source.PixelWidth;
        var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        transformed.Freeze();
        return transformed;
    }

    private static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        encoder.Save(stream);
    }

    private static BitmapSource Freeze(BitmapSource image)
    {
        if (image.IsFrozen)
        {
            return image;
        }

        if (!image.CanFreeze)
        {
            return image;
        }

        var clone = image.Clone();
        clone.Freeze();
        return clone;
    }

    /// <summary>
    /// Encodes <paramref name="image"/> as a full-size PNG in memory. Public so callers (e.g.
    /// CapturePipeline, which needs the exact bytes to hash before deciding whether to save at
    /// all) can produce the same bytes <see cref="SaveImage(BitmapSource)"/> would write, and
    /// hand them to <see cref="SaveImage(byte[], BitmapSource)"/> afterward without encoding
    /// twice.
    /// </summary>
    public static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
