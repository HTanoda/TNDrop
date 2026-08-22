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
    private bool _stackThumbnailLoaded;
    private ImageSource? _stackThumbnail;
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
        // Global Constraints decision, unchanged by v1.2: Media/IsMediaFile answer "does this
        // card show a large shell thumbnail INSTEAD OF its icon", which stays a lone-file-only
        // question even though v1.2 widened the SEPARATE "which filter tab counts this card"
        // question -- see ShelfViewModel.IsImageEntity's own doc comment for that split).
        Media = Kind == ClipKind.Files && item.Paths.Count == 1
            ? MediaKind.Classify(item.Paths[0])
            : MediaCategory.Other;

        // Same one-resolution rationale, scoped to a stack's first path instead: StackThumbnail
        // and the video-badge trigger in Cards.xaml both need "what does Paths[0] classify as"
        // for a stack, so it is computed once here rather than each re-deriving its own answer.
        // Other() for every non-stack card, same as Media.
        StackFirstMedia = IsStack ? MediaKind.Classify(item.Paths[0]) : MediaCategory.Other;
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
    /// stack's icon is always the first path's, per the display spec), AND for a lone media file
    /// whose <see cref="Thumbnail"/> already came back null (the Cards.xaml null-Thumbnail
    /// fallback, v1.1 review item #2) -- a shell that cannot produce a 256px preview can often
    /// still produce a 32px type icon, and the fallback should use it instead of the generic glyph
    /// when one is available. Null for Image cards, and null when the shell has nothing to offer
    /// either way -- callers must fall back to the existing generic glyph in that case. Lazy and
    /// cached the same way as <see cref="Thumbnail"/>.</summary>
    public ImageSource? FileIcon
    {
        get
        {
            if (!_fileIconLoaded)
            {
                _fileIconLoaded = true;

                if (ShouldTryFileIcon())
                {
                    _fileIcon = ShellImaging.GetIcon(Item.Paths[0], FileIconPx);
                }
            }

            return _fileIcon;
        }
    }

    /// <summary>
    /// True when a 32px shell icon is worth attempting for this card's first path: every ordinary
    /// non-media Files card (the original rule), OR a media file whose Thumbnail has ALREADY been
    /// requested and came back null.
    /// <para>Deliberately reads the already-cached <see cref="_thumbnailLoaded"/>/
    /// <see cref="_thumbnail"/> fields here, not the <see cref="Thumbnail"/> property itself:
    /// calling the property from inside this getter would force today's 256px shell round-trip as
    /// a side effect of asking for a 32px icon, even for a caller that never otherwise needed the
    /// large thumbnail at all -- exactly the eager load the review flagged. The trade-off (accepted,
    /// not hidden): this only helps once something else has already read Thumbnail first. In
    /// practice that is guaranteed for the one caller that matters -- Cards.xaml's own
    /// null-Thumbnail MultiDataTrigger binds Thumbnail as one of its own conditions, so by the time
    /// the fallback UI needs an icon, Thumbnail has already resolved. A hypothetical future caller
    /// that reads FileIcon on a media-file card without ever touching Thumbnail first would still
    /// get null here, same as before this fix.</para>
    /// </summary>
    private bool ShouldTryFileIcon() =>
        Kind == ClipKind.Files
        && Item.Paths.Count > 0
        && (!IsMediaFile || (_thumbnailLoaded && _thumbnail is null));

    public bool IsStack => Item.IsStack;

    public int StackCount => Item.Paths.Count;

    /// <summary>What <c>Item.Paths[0]</c> classifies as, for a stack card only --
    /// <see cref="MediaCategory.Other"/> for every non-stack card, computed once at construction
    /// (see the constructor's remarks). Drives both <see cref="StackThumbnail"/> and the
    /// video-overlay badge on the stack's large-thumbnail visual in Cards.xaml.</summary>
    public MediaCategory StackFirstMedia { get; }

    /// <summary>
    /// The shell's large preview of a stack's first path, at the same <see cref="MediaThumbnailPx"/>
    /// size and via the same lazy-loaded-and-cached pattern as <see cref="Thumbnail"/> -- null
    /// (and untouched) until first read, then cached for the lifetime of this instance. Non-null
    /// only for a stack (<see cref="IsStack"/>) whose first path classifies as an image or a video
    /// (<see cref="StackFirstMedia"/> != <see cref="MediaCategory.Other"/>); null for every other
    /// card, including a lone media file (which already has its own large thumbnail via
    /// <see cref="Thumbnail"/>).
    ///
    /// <para><b>Load-order / independence.</b> This property owns its own
    /// <c>_stackThumbnailLoaded</c>/<c>_stackThumbnail</c> fields, entirely separate from
    /// <see cref="Thumbnail"/>'s and <see cref="FileIcon"/>'s -- reading StackThumbnail never
    /// forces either of those to resolve, and vice versa. In practice the three never compete for
    /// the same card anyway: <see cref="IsMediaFile"/> (which gates Thumbnail's media branch) and
    /// <see cref="IsStack"/> (which gates this property) are mutually exclusive by definition
    /// (Item.Paths.Count == 1 vs > 1), and <see cref="ShouldTryFileIcon"/> only reads the already-
    /// cached Thumbnail fields, never this one -- so a stack's FileIcon attempt is unaffected by
    /// whether StackThumbnail has been read yet.</para>
    /// </summary>
    public ImageSource? StackThumbnail
    {
        get
        {
            if (!_stackThumbnailLoaded)
            {
                _stackThumbnailLoaded = true;

                if (StackFirstMedia != MediaCategory.Other)
                {
                    _stackThumbnail = ShellImaging.GetThumbnail(Item.Paths[0], MediaThumbnailPx);
                }
            }

            return _stackThumbnail;
        }
    }

    /// <summary>True once <see cref="StackThumbnail"/> resolves to a real image. Exists purely so
    /// Cards.xaml can pick the large-thumbnail stack visual with a plain DataTrigger: WPF triggers
    /// only match a literal value, not "is not null", so a boolean stand-in is needed the same way
    /// <see cref="IsMediaFile"/> stands in for "Media != Other" elsewhere in this class. Reading
    /// this forces the same lazy shell round-trip <see cref="StackThumbnail"/> itself would.</summary>
    public bool HasStackThumbnail => StackThumbnail != null;

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
