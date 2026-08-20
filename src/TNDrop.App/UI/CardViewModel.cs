using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using TNDrop.Core;
using TNDrop.Platform;
using TNDrop.Resources;
using TNDrop.Services;

namespace TNDrop.UI;

/// <summary>
/// Display wrapper around a single <see cref="ClipItem"/>. Title/Subtitle are computed once at
/// construction (the item itself is immutable from the VM's point of view -- a changed item means
/// a new ClipItem instance and therefore a new CardViewModel, rebuilt by <see cref="ShelfViewModel"/>).
/// Thumbnail is lazily loaded on first access so scrolling past collapsed/virtualized cards never
/// touches disk.
/// </summary>
public sealed class CardViewModel : INotifyPropertyChanged
{
    /// <summary>Large-thumbnail px passed to <see cref="ShellImaging.GetThumbnail"/> for a lone
    /// media file's card, matching the display spec's "同様の大サムネイル".</summary>
    private const int MediaThumbnailPx = 256;

    /// <summary>Shell icon px for the 32px logical left-slot icon (non-media files and stacks).</summary>
    private const int FileIconPx = 32;

    private readonly ThumbnailService? _thumbnailService;
    private bool _thumbnailLoaded;
    private ImageSource? _thumbnail;
    private bool _fileIconLoaded;
    private ImageSource? _fileIcon;
    private bool _selected;

    public CardViewModel(ClipItem item, ThumbnailService? thumbnailService = null)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        _thumbnailService = thumbnailService;
        Pinned = item.Pinned;
        (Title, Subtitle) = BuildTitleAndSubtitle(item);

        // Single resolution: Media is derived once here, from Item.Paths alone, and every other
        // property on this class (IsMediaFile, Thumbnail, FileIcon) reads only Media -- never
        // re-classifies the path itself -- so they can never disagree about what this card is.
        // Kind==Files with exactly one path is the only case that can be a lone media file; a
        // stack (2+ paths) always stays Other here even if every path in it is an image (the v1.1
        // Global Constraints decision: a stack is always ファイル, never 画像).
        Media = Kind == ClipKind.Files && item.Paths.Count == 1
            ? MediaKind.Classify(item.Paths[0])
            : MediaCategory.Other;
    }

    public ClipItem Item { get; }

    public string Id => Item.Id;

    public ClipKind Kind => Item.Kind;

    /// <summary>Snapshot of Item.Pinned taken at construction time. ShelfViewModel rebuilds cards
    /// whenever the store changes (including pin toggles), so this never goes stale in practice.</summary>
    public bool Pinned { get; }

    /// <summary>Multi-select flag, wired up by Task 15. ShelfViewModel preserves this by Id across rebuilds.</summary>
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value)
            {
                return;
            }

            _selected = value;
            OnPropertyChanged();
        }
    }

    public string Title { get; }

    public string Subtitle { get; }

    /// <summary>What this card's single file (if any) is, by extension: Image/Video/Other. Only
    /// meaningful when Kind==Files and there is exactly one path -- a stack is always Other here
    /// regardless of what its paths look like. Computed once at construction; see the
    /// constructor's remarks for why every other classification-driven property reads this
    /// instead of re-deriving its own answer.</summary>
    public MediaCategory Media { get; }

    /// <summary>True for a lone Files card whose single path is an image or a video -- the case
    /// that gets the large shell thumbnail instead of the small extension icon. Always false for
    /// a stack (Item.Paths.Count > 1), even an all-images one: per the v1.1 Global Constraints, a
    /// stack stays ファイル and keeps its layered-paper look.</summary>
    public bool IsMediaFile => Kind == ClipKind.Files && Item.Paths.Count == 1 && Media != MediaCategory.Other;

    /// <summary>
    /// (a) Kind==Image: the existing blob thumbnail from <see cref="ThumbnailService"/>, unchanged.
    /// (b) <see cref="IsMediaFile"/>: the shell's large preview of the single path, via
    /// <see cref="ShellImaging.GetThumbnail"/> at <see cref="MediaThumbnailPx"/>. Null (and
    /// untouched) for every other card. Loaded lazily on first access and cached for the lifetime
    /// of this instance -- ShellImaging has its own cache underneath, but that lookup still costs
    /// a dictionary hit per access, which this flag avoids paying more than once per card.
    /// </summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (!_thumbnailLoaded)
            {
                _thumbnailLoaded = true;

                if (Kind == ClipKind.Image && _thumbnailService != null)
                {
                    _thumbnail = _thumbnailService.LoadThumb(Item.ThumbFile);
                }
                else if (IsMediaFile)
                {
                    _thumbnail = ShellImaging.GetThumbnail(Item.Paths[0], MediaThumbnailPx);
                }
            }

            return _thumbnail;
        }
    }

    /// <summary>Shell extension icon for the left slot of a non-media single file or a stack (the
    /// stack's icon is always the first path's, per the display spec). Null for Image cards and
    /// for a lone media file (those use <see cref="Thumbnail"/> instead), and null when the shell
    /// has nothing to offer -- callers must fall back to the existing generic glyph in that case.
    /// Lazy and cached the same way as <see cref="Thumbnail"/>.</summary>
    public ImageSource? FileIcon
    {
        get
        {
            if (!_fileIconLoaded)
            {
                _fileIconLoaded = true;

                if (Kind == ClipKind.Files && !IsMediaFile && Item.Paths.Count > 0)
                {
                    _fileIcon = ShellImaging.GetIcon(Item.Paths[0], FileIconPx);
                }
            }

            return _fileIcon;
        }
    }

    public bool IsStack => Kind == ClipKind.Files && Item.Paths.Count > 1;

    public int StackCount => Item.Paths.Count;

    private static (string Title, string Subtitle) BuildTitleAndSubtitle(ClipItem item)
    {
        switch (item.Kind)
        {
            case ClipKind.Link:
                var url = item.Text ?? string.Empty;
                return (UrlDetector.GetDomain(url), url);

            case ClipKind.Image:
                return (Strings.CardImage, string.Empty);

            case ClipKind.Files:
                var firstPath = item.Paths.Count > 0 ? item.Paths[0] : string.Empty;
                var subtitle = firstPath.Length > 0 ? firstPath : string.Empty;
                var title = item.Paths.Count == 1
                    ? Path.GetFileName(firstPath)
                    : string.Format(Strings.CardFilesCountFormat, item.Paths.Count);
                return (title, subtitle);

            case ClipKind.Text:
            default:
                var text = item.Text ?? string.Empty;
                var singleLine = ToSingleLine(text);
                var truncated = singleLine.Length > 120 ? singleLine[..120] : singleLine;
                return (truncated, string.Format(Strings.CardCharCountFormat, text.Length));
        }
    }

    private static string ToSingleLine(string text) =>
        text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
