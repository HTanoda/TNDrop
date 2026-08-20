using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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

    private readonly string _blobsDir;
    private readonly HashSet<string> _warnedMissing = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _warnLock = new();

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
    /// </summary>
    public (string imageFile, string thumbFile) SaveImage(BitmapSource image)
    {
        if (image is null)
        {
            throw new ArgumentNullException(nameof(image));
        }

        var frozenSource = image;
        if (!frozenSource.IsFrozen && frozenSource.CanFreeze)
        {
            frozenSource = frozenSource.Clone();
            frozenSource.Freeze();
        }

        var id = Guid.NewGuid().ToString("N");
        var imageFile = $"{id}.png";
        var thumbFile = $"{id}_thumb.png";

        SavePng(frozenSource, Path.Combine(_blobsDir, imageFile));

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
}
