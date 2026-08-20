using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using TNDrop.Core;
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
    private readonly ThumbnailService? _thumbnailService;
    private bool _thumbnailLoaded;
    private ImageSource? _thumbnail;
    private bool _selected;

    public CardViewModel(ClipItem item, ThumbnailService? thumbnailService = null)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        _thumbnailService = thumbnailService;
        Pinned = item.Pinned;
        (Title, Subtitle) = BuildTitleAndSubtitle(item);
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

    /// <summary>Image cards only; null (and untouched) for every other kind. Loaded lazily from
    /// disk on first access and cached for the lifetime of this instance.</summary>
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
            }

            return _thumbnail;
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
