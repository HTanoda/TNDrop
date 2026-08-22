using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using TNDrop.Core;
using TNDrop.Platform;
using TNDrop.Resources;
using TNDrop.Services;

namespace TNDrop.UI;

/// <summary>
/// One line of the stack flyout: the thumbnail/icon, the file name, and the size (or the "file not
/// found" notice). Everything except <see cref="Thumbnail"/> is immutable -- the flyout rebuilds
/// its rows every time it opens, and closes itself as soon as the underlying stack changes, so a
/// row never has to update most of itself in place.
///
/// <para><b>Thumbnail is the one mutable, deferred exception (v1.3 Task C review fix).</b>
/// <see cref="Create"/> does NOT call into <see cref="ShellImaging"/> -- that is a potentially slow,
/// COM-based shell round-trip, and doing it synchronously for every row on the UI thread at flyout-
/// open time widened the v1.1-documented "can stall on an unreachable UNC share" trade-off
/// (<see cref="ShellImaging"/>'s own class remarks) from "per card" to "per flyout open, x10,
/// synchronously". Instead <see cref="Create"/> only decides <see cref="NeedsThumbnail"/> (a cheap,
/// already-probed fact) and leaves <see cref="Thumbnail"/> null; <c>StackFlyout.ShowFor</c> is what
/// schedules the actual <see cref="ResolveThumbnail"/> call on a background thread and applies the
/// result back via <see cref="ApplyThumbnail"/> once it lands (guarding against a stale result from
/// a flyout that has since closed or moved on to a different stack -- see StackFlyout's own
/// remarks). This class stays deliberately ignorant of THAT scheduling/threading policy; it only
/// exposes the pure resolve function and a settable property with change notification.</para>
/// </summary>
public sealed class StackFileRow : INotifyPropertyChanged
{
    private const string Module = "StackFileRow";

    private const string FileGlyph = "\U0001F4C4";     // page
    private const string FolderGlyph = "\U0001F4C1";   // folder
    private const string MissingGlyph = "\u26A0";      // warning sign

    /// <summary>Row thumbnail/icon size (v1.3 Task C), matching the design doc's "32px \u524D\u5F8C".</summary>
    private const int ThumbnailPx = 32;

    private ImageSource? _thumbnail;

    private StackFileRow(string path, string fileName, string icon, string sizeText, bool exists,
        bool needsThumbnail)
    {
        Path = path;
        FileName = fileName;
        Icon = icon;
        SizeText = sizeText;
        Exists = exists;
        NeedsThumbnail = needsThumbnail;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Full path, exactly as the stack stores it. This is what a row drag/click carries.</summary>
    public string Path { get; }

    public string FileName { get; }

    /// <summary>Text glyph fallback, shown whenever <see cref="Thumbnail"/> is null: before a
    /// background resolution has landed, for a missing file (no resolution is ever attempted), or
    /// once resolution completed and the shell had nothing to offer for this path.</summary>
    public string Icon { get; }

    /// <summary>
    /// The shell's artwork for this row's left slot (v1.3 Task C): a real preview via
    /// <see cref="ShellImaging.GetThumbnail"/> for an image or video path (<see cref="MediaKind"/>),
    /// the shell's extension icon via <see cref="ShellImaging.GetIcon"/> for everything else. Both
    /// go through ShellImaging's own LRU cache (the same one CardViewModel's Thumbnail/FileIcon use)
    /// rather than a second cache living here. Null until a background resolution (see the class
    /// remarks) applies a result via <see cref="ApplyThumbnail"/> -- including permanently null for
    /// a missing/directory path (<see cref="NeedsThumbnail"/> is false, so nothing is ever
    /// scheduled) or when the shell had nothing to offer, in which case <see cref="Icon"/> is the
    /// fallback XAML binds to instead.
    /// </summary>
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            if (ReferenceEquals(_thumbnail, value))
            {
                return;
            }

            _thumbnail = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Formatted size for a file, empty for a directory, the FileMissing notice when gone.</summary>
    public string SizeText { get; }

    /// <summary>False greys the whole row out (see the DataTrigger in StackFlyout.xaml).</summary>
    public bool Exists { get; }

    /// <summary>True when this row is a real, existing, non-directory path -- the only case a
    /// thumbnail/icon is worth resolving for at all. Decided once at <see cref="Create"/> from the
    /// same probe that already determined <see cref="Exists"/>, so the caller scheduling background
    /// work never has to re-probe the file system itself just to ask "should I bother".</summary>
    public bool NeedsThumbnail { get; }

    /// <summary>
    /// Builds the row from ONE probe of the path. Icon, size text and <see cref="Exists"/> are three
    /// views of the same fact and are derived together on purpose: resolved separately, a file
    /// deleted between two <c>File.Exists</c> calls would render as, say, a normal page icon with a
    /// "file not found" size. Does NOT touch <see cref="ShellImaging"/> -- see the class remarks.
    /// </summary>
    public static StackFileRow Create(string path)
    {
        path ??= string.Empty;

        var (exists, isDirectory, length) = Probe(path);

        var icon = !exists ? MissingGlyph : isDirectory ? FolderGlyph : FileGlyph;
        var size = !exists ? Strings.FileMissing : isDirectory ? string.Empty : FormatSize(length);

        return new StackFileRow(path, NameOf(path), icon, size, exists, needsThumbnail: exists && !isDirectory);
    }

    /// <summary>
    /// The actual (potentially slow, COM-based) shell round-trip: image/video paths
    /// (<see cref="MediaKind.Classify"/>) get the shell's real preview, every other existing file
    /// gets the shell's extension icon instead -- never the reverse, and never both attempted for
    /// the same row. A converted screenshot blob (v1.3 Task B) is an ordinary PNG under blobs\ by
    /// the time it reaches here, so it is classified and thumbnailed exactly like any other image
    /// file, with no separate code path.
    ///
    /// <para>Pure with respect to this instance (does not read or write any row) and safe to call
    /// off the UI thread by design -- this is the method <c>StackFlyout</c> hands to its background
    /// worker. Callers must apply the result back via <see cref="ApplyThumbnail"/> from the UI
    /// thread themselves; this method has no notion of which row (if any) it was called for.</para>
    /// </summary>
    public static ImageSource? ResolveThumbnail(string path) =>
        MediaKind.IsMedia(path)
            ? ShellImaging.GetThumbnail(path, ThumbnailPx)
            : ShellImaging.GetIcon(path, ThumbnailPx);

    /// <summary>
    /// Applies a background-resolved thumbnail/icon to this row, raising
    /// <see cref="PropertyChanged"/> so the flyout's bound Image swaps in automatically. Must be
    /// called on the UI thread (WPF data-binding requires it) and only after the caller has
    /// confirmed the result is not stale -- see <c>StackFlyout.ShowFor</c>'s own remarks on the
    /// generation/stack-id guard. Freezing is the caller's responsibility (both
    /// <see cref="ShellImaging.GetThumbnail"/> and <see cref="ShellImaging.GetIcon"/> already
    /// return frozen images, so there is nothing left to do here).
    /// </summary>
    public void ApplyThumbnail(ImageSource? thumbnail) => Thumbnail = thumbnail;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

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
