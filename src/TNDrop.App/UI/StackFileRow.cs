using System;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using TNDrop.Core;
using TNDrop.Platform;
using TNDrop.Resources;
using TNDrop.Services;

namespace TNDrop.UI;

/// <summary>
/// One line of the stack flyout: the thumbnail/icon, the file name, and the size (or the "file not
/// found" notice). Immutable -- the flyout rebuilds its rows every time it opens, and closes itself
/// as soon as the underlying stack changes, so a row never has to update itself in place.
/// </summary>
public sealed class StackFileRow
{
    private const string Module = "StackFileRow";

    private const string FileGlyph = "\U0001F4C4";     // page
    private const string FolderGlyph = "\U0001F4C1";   // folder
    private const string MissingGlyph = "\u26A0";      // warning sign

    /// <summary>Row thumbnail/icon size (v1.3 Task C), matching the design doc's "32px \u524D\u5F8C".</summary>
    private const int ThumbnailPx = 32;

    private StackFileRow(string path, string fileName, string icon, string sizeText, bool exists,
        ImageSource? thumbnail)
    {
        Path = path;
        FileName = fileName;
        Icon = icon;
        SizeText = sizeText;
        Exists = exists;
        Thumbnail = thumbnail;
    }

    /// <summary>Full path, exactly as the stack stores it. This is what a row drag/click carries.</summary>
    public string Path { get; }

    public string FileName { get; }

    /// <summary>Text glyph fallback, shown when <see cref="Thumbnail"/> is null (missing file, or
    /// the shell had nothing to offer for this path).</summary>
    public string Icon { get; }

    /// <summary>
    /// The shell's artwork for this row's left slot (v1.3 Task C): a real preview via
    /// <see cref="ShellImaging.GetThumbnail"/> for an image or video path (<see cref="MediaKind"/>),
    /// the shell's extension icon via <see cref="ShellImaging.GetIcon"/> for everything else. Both
    /// go through ShellImaging's own LRU cache (the same one CardViewModel's Thumbnail/FileIcon use)
    /// rather than a second cache living here. Null for a missing path (no probe attempted -- there
    /// is nothing on disk to ask the shell about) and null whenever the shell has nothing to offer,
    /// in which case <see cref="Icon"/> is the fallback XAML binds to instead.
    /// </summary>
    public ImageSource? Thumbnail { get; }

    /// <summary>Formatted size for a file, empty for a directory, the FileMissing notice when gone.</summary>
    public string SizeText { get; }

    /// <summary>False greys the whole row out (see the DataTrigger in StackFlyout.xaml).</summary>
    public bool Exists { get; }

    /// <summary>
    /// Builds the row from ONE probe of the path. Icon, size text and <see cref="Exists"/> are three
    /// views of the same fact and are derived together on purpose: resolved separately, a file
    /// deleted between two <c>File.Exists</c> calls would render as, say, a normal page icon with a
    /// "file not found" size.
    /// </summary>
    public static StackFileRow Create(string path)
    {
        path ??= string.Empty;

        var (exists, isDirectory, length) = Probe(path);

        var icon = !exists ? MissingGlyph : isDirectory ? FolderGlyph : FileGlyph;
        var size = !exists ? Strings.FileMissing : isDirectory ? string.Empty : FormatSize(length);
        var thumbnail = exists && !isDirectory ? LoadThumbnail(path) : null;

        return new StackFileRow(path, NameOf(path), icon, size, exists, thumbnail);
    }

    /// <summary>
    /// Image/video paths (<see cref="MediaKind.Classify"/>) get the shell's real preview; every
    /// other existing file gets the shell's extension icon instead -- never the reverse, and never
    /// both attempted for the same row. A converted screenshot blob (v1.3 Task B) is an ordinary PNG
    /// under blobs\ by the time it reaches here, so it is classified and thumbnailed exactly like
    /// any other image file, with no separate code path.
    /// </summary>
    private static ImageSource? LoadThumbnail(string path) =>
        MediaKind.IsMedia(path)
            ? ShellImaging.GetThumbnail(path, ThumbnailPx)
            : ShellImaging.GetIcon(path, ThumbnailPx);

    /// <summary>
    /// Human-readable byte count. Invariant formatting on purpose: the unit suffixes are not
    /// localized either, and a size that reads "1,5 MB" on one machine and "1.5 MB" on the next
    /// would be the only number in the UI that moved.
    /// </summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 0)
        {
            bytes = 0;
        }

        const long kilo = 1024;
        const long mega = kilo * 1024;
        const long giga = mega * 1024;

        if (bytes < kilo)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        if (bytes < mega)
        {
            return Scaled(bytes, kilo) + " KB";
        }

        if (bytes < giga)
        {
            return Scaled(bytes, mega) + " MB";
        }

        return Scaled(bytes, giga) + " GB";
    }

    private static string Scaled(long bytes, long unit) =>
        ((double)bytes / unit).ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>
    /// Existence comes from <see cref="DragDropSource.PathExists"/> -- the same rule the drag
    /// payload and the click-to-copy path use -- so a row cannot claim a file is there that a drag
    /// of that very row would then refuse to carry. Only the size/kind detail is probed here.
    /// </summary>
    private static (bool Exists, bool IsDirectory, long Length) Probe(string path)
    {
        if (!DragDropSource.PathExists(path))
        {
            return (false, false, 0);
        }

        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (true, false, info.Length) : (true, true, 0);
        }
        catch (Exception ex)
        {
            // It exists (PathExists just said so) but we cannot size it -- a permission-denied
            // share, a path we cannot stat. Render it as a directory: present, no size.
            // ex.Message embeds the full path, so log only the exception type -- clipboard-derived
            // paths must not land in logs/app-YYYYMMDD.log.
            FileLogger.Instance?.Warn(Module, $"could not read the size of a stacked path: {ex.GetType().Name}");
            return (true, true, 0);
        }
    }

    /// <summary>
    /// Leaf name, falling back to the whole path. GetFileName comes back empty for a drive root
    /// ("D:\") or a trailing separator, and a row with a blank name is unusable.
    /// </summary>
    private static string NameOf(string path)
    {
        try
        {
            var name = System.IO.Path.GetFileName(path.TrimEnd(
                System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            return string.IsNullOrEmpty(name) ? path : name;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }
}
