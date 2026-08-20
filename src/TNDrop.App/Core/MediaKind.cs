using System;
using System.Collections.Generic;
using System.IO;

namespace TNDrop.Core;

/// <summary>
/// How a file path should be presented on a card: a still image, a video, or anything else.
/// </summary>
public enum MediaCategory
{
    /// <summary>A still-image file. A lone one is filtered as 画像 and shown as a big thumbnail.</summary>
    Image,

    /// <summary>A video file. Stays a ファイル for filtering, but is shown as a big thumbnail.</summary>
    Video,

    /// <summary>Everything else: shown with a small shell extension icon in the left slot.</summary>
    Other,
}

/// <summary>
/// Classifies a path into a <see cref="MediaCategory"/> from its extension alone.
///
/// <para>Pure and I/O-free by design: it is called while building card view models, once per file
/// per rebuild, and must never touch the disk (a path on a disconnected network share would
/// otherwise stall the UI thread). The consequence is that it judges the name, not the bytes --
/// a directory literally named "Photos.png" classifies as <see cref="MediaCategory.Image"/>, and
/// a JPEG saved as "scan.dat" classifies as <see cref="MediaCategory.Other"/>. Callers that need
/// to know whether the path is a directory must check that themselves.</para>
///
/// <para>The two extension lists are fixed by the v1.1 plan's Global Constraints; do not extend
/// them here without updating that decision, since they also drive which cards count as ファイル
/// in the shelf's filter bar.</para>
/// </summary>
public static class MediaKind
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.Ordinal)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff", ".heic",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.Ordinal)
    {
        ".mp4", ".mov", ".avi", ".wmv", ".mkv", ".webm",
    };

    /// <summary>
    /// The category <paramref name="path"/>'s extension implies. Never throws; null, empty,
    /// whitespace, an extensionless name and a directory-looking path all return
    /// <see cref="MediaCategory.Other"/>.
    /// </summary>
    public static MediaCategory Classify(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return MediaCategory.Other;
        }

        // Ordinal-lowercased once, then matched against ordinal sets: the extension lists are
        // pure ASCII, so an invariant/culture-aware comparison would only add turkish-I style
        // surprises for no benefit.
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext.Length == 0)
        {
            return MediaCategory.Other;
        }

        if (ImageExtensions.Contains(ext))
        {
            return MediaCategory.Image;
        }

        return VideoExtensions.Contains(ext) ? MediaCategory.Video : MediaCategory.Other;
    }

    /// <summary>
    /// True when the path is an image or a video, i.e. the card should show a large shell
    /// thumbnail rather than a small extension icon.
    /// </summary>
    public static bool IsMedia(string? path) => Classify(path) != MediaCategory.Other;
}
