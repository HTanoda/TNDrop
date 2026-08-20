using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using TNDrop.Core;
using TNDrop.Services;

namespace TNDrop.UI;

public enum CardFilter { All, Text, Links, Images, Files }

/// <summary>
/// Backs the shelf's card list: filter tabs, search box, the pinned deck and the main card list.
/// Rebuilds its two card collections from scratch every time the store changes or the
/// filter/search changes; there is no incremental diffing, so <see cref="CardViewModel.Selected"/>
/// is the only per-card state carried across a rebuild (matched by Id).
/// </summary>
public sealed class ShelfViewModel : INotifyPropertyChanged
{
    private readonly ItemStore _store;
    private readonly ThumbnailService? _thumbnailService;

    private CardFilter _filter = CardFilter.All;
    private string _searchText = string.Empty;
    private int _countAll;
    private int _countText;
    private int _countLinks;
    private int _countImages;
    private int _countFiles;

    public ShelfViewModel(ItemStore store)
        : this(store, CreateThumbnailService(store))
    {
    }

    /// <summary>Test/advanced-composition entry point: pass a pre-built (or null)
    /// ThumbnailService instead of one derived from store.BlobsDir.</summary>
    internal ShelfViewModel(ItemStore store, ThumbnailService? thumbnailService)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _thumbnailService = thumbnailService;

        _store.Changed += OnStoreChanged;
        Rebuild();
    }

    public ObservableCollection<CardViewModel> Cards { get; } = new();

    public ObservableCollection<CardViewModel> PinnedCards { get; } = new();

    public CardFilter Filter
    {
        get => _filter;
        set
        {
            if (_filter == value)
            {
                return;
            }

            _filter = value;
            OnPropertyChanged();
            Rebuild();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            value ??= string.Empty;
            if (_searchText == value)
            {
                return;
            }

            _searchText = value;
            OnPropertyChanged();
            Rebuild();
        }
    }

    public int CountAll => _countAll;

    public int CountText => _countText;

    public int CountLinks => _countLinks;

    public int CountImages => _countImages;

    public int CountFiles => _countFiles;

    /// <summary>True while at least one card (visible or pinned) has <see cref="CardViewModel.Selected"/>
    /// set. Drives the batch action bar in ShelfWindow.</summary>
    public bool SelectionMode => SelectedCount > 0;

    /// <summary>Count of currently selected cards across both <see cref="Cards"/> and
    /// <see cref="PinnedCards"/>.</summary>
    public int SelectedCount => Cards.Concat(PinnedCards).Count(c => c.Selected);

    /// <summary>Flips the Selected flag of the card with this Id, wherever it lives (visible or
    /// pinned) -- Ctrl+click and "plain click while in selection mode" both route through this,
    /// so the two gestures can never disagree about what toggling means.</summary>
    public void ToggleSelected(string id)
    {
        var card = FindCard(id);
        if (card is null)
        {
            return;
        }

        card.Selected = !card.Selected;
        RaiseSelectionChanged();
    }

    /// <summary>Selects every card in <see cref="Cards"/> -- i.e. exactly what the current
    /// filter+search shows -- and nothing else. Pinned cards are deliberately left alone: "全選択"
    /// selects the visible unpinned deck, matching <see cref="ClearVisible"/>'s own scope.</summary>
    public void SelectAllVisible()
    {
        foreach (var card in Cards)
        {
            card.Selected = true;
        }

        RaiseSelectionChanged();
    }

    /// <summary>Deselects every card, visible or pinned.</summary>
    public void ClearSelection()
    {
        foreach (var card in Cards.Concat(PinnedCards))
        {
            card.Selected = false;
        }

        RaiseSelectionChanged();
    }

    /// <summary>The underlying <see cref="ClipItem"/> of every currently selected card.</summary>
    public List<ClipItem> GetSelectedItems() =>
        Cards.Concat(PinnedCards).Where(c => c.Selected).Select(c => c.Item).ToList();

    /// <summary>Removes every selected item from the store (pinned or not -- an explicit selection
    /// overrides the pin) and persists immediately, since a batch delete has no other save point.
    /// A no-op when nothing is selected. <see cref="SelectionMode"/> returns to false afterwards
    /// because the removed ids no longer exist for the following rebuild to preserve.</summary>
    public void RemoveSelected()
    {
        var ids = Cards.Concat(PinnedCards).Where(c => c.Selected).Select(c => c.Id).ToList();
        if (ids.Count == 0)
        {
            return;
        }

        _store.RemoveMany(ids);
        _store.Save();
    }

    private CardViewModel? FindCard(string id) =>
        Cards.FirstOrDefault(c => c.Id == id) ?? PinnedCards.FirstOrDefault(c => c.Id == id);

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectionMode));
        OnPropertyChanged(nameof(SelectedCount));
    }

    /// <summary>Deletes every unpinned item matching the current filter+search -- i.e. exactly
    /// what's in <see cref="Cards"/> right now. Caller (ShelfWindow) is responsible for
    /// confirming with the user first. Persists immediately, same rationale as
    /// <see cref="RemoveSelected"/>: a crash before exit must not resurrect a cleared item.</summary>
    public void ClearVisible()
    {
        var ids = Cards.Select(c => c.Id).ToList();
        if (ids.Count == 0)
        {
            return;
        }

        _store.RemoveMany(ids);
        _store.Save();
    }

    private static ThumbnailService? CreateThumbnailService(ItemStore store)
    {
        if (store is null)
        {
            return null;
        }

        try
        {
            return new ThumbnailService(store.BlobsDir);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Raised immediately before a store-driven rebuild (i.e. one triggered by
    /// <see cref="ItemStore.Changed"/> -- a background clipboard capture, a pin toggle, a
    /// delete -- as opposed to the user changing <see cref="Filter"/> or <see cref="SearchText"/>,
    /// where jumping the list back to the top is expected). ShelfWindow uses this pair to save and
    /// restore the card list's scroll position around the Cards/PinnedCards Clear()+repopulate,
    /// which would otherwise silently reset scroll to the top on every background change.
    /// </summary>
    internal event Action? StoreRebuilding;

    /// <summary>Raised immediately after a store-driven rebuild completes. See <see cref="StoreRebuilding"/>.</summary>
    internal event Action? StoreRebuilt;

    private void OnStoreChanged()
    {
        // ItemStore.Changed can be raised from a worker thread (e.g. the clipboard monitor) as
        // well as the UI thread. ObservableCollection mutations must happen on the UI thread, so
        // marshal there -- except in unit tests, where Application.Current is null and there is
        // no Dispatcher to marshal to; run synchronously in that case.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            RebuildFromStore();
        }
        else
        {
            dispatcher.Invoke(RebuildFromStore);
        }
    }

    private void RebuildFromStore()
    {
        StoreRebuilding?.Invoke();
        Rebuild();
        StoreRebuilt?.Invoke();
    }

    private void Rebuild()
    {
        var selectedIds = new HashSet<string>(
            Cards.Concat(PinnedCards).Where(c => c.Selected).Select(c => c.Id));

        var items = _store.Items; // newest-first snapshot
        var pinnedItems = items.Where(i => i.Pinned).ToList();
        var unpinnedItems = items.Where(i => !i.Pinned).ToList();

        var searched = unpinnedItems.Where(i => MatchesSearch(i, _searchText)).ToList();

        _countAll = searched.Count;
        _countText = searched.Count(i => i.Kind == ClipKind.Text);
        _countLinks = searched.Count(i => i.Kind == ClipKind.Link);
        _countImages = searched.Count(i => i.Kind == ClipKind.Image || IsSingleImageFile(i));
        _countFiles = searched.Count(i => i.Kind == ClipKind.Files && !IsSingleImageFile(i));

        var visible = searched.Where(i => MatchesFilter(i, _filter)).ToList();

        Cards.Clear();
        foreach (var item in visible)
        {
            Cards.Add(MakeCard(item, selectedIds));
        }

        // The pinned deck always shows every pinned item, independent of the current
        // filter/search -- pinning something is how the user keeps it reachable regardless of
        // what they're currently filtering/searching for.
        PinnedCards.Clear();
        foreach (var item in pinnedItems)
        {
            PinnedCards.Add(MakeCard(item, selectedIds));
        }

        OnPropertyChanged(nameof(CountAll));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(CountLinks));
        OnPropertyChanged(nameof(CountImages));
        OnPropertyChanged(nameof(CountFiles));

        // A rebuild can change SelectedCount/SelectionMode even though nothing here calls
        // ToggleSelected/etc: a selected item can be removed by another path (the per-card Delete
        // button, a merge), or narrowed out of Cards by a Filter/SearchText change. Selected is
        // preserved by Id above; anything not carried over here must still update the bar.
        RaiseSelectionChanged();
    }

    private CardViewModel MakeCard(ClipItem item, HashSet<string> selectedIds)
    {
        var card = new CardViewModel(item, _thumbnailService);
        if (selectedIds.Contains(card.Id))
        {
            card.Selected = true;
        }

        return card;
    }

    private static bool MatchesFilter(ClipItem item, CardFilter filter) => filter switch
    {
        CardFilter.All => true,
        CardFilter.Text => item.Kind == ClipKind.Text,
        CardFilter.Links => item.Kind == ClipKind.Link,
        CardFilter.Images => item.Kind == ClipKind.Image || IsSingleImageFile(item),
        CardFilter.Files => item.Kind == ClipKind.Files && !IsSingleImageFile(item),
        _ => true,
    };

    /// <summary>
    /// SINGLE RESOLUTION for "does this item count as 画像 instead of ファイル": both
    /// <see cref="MatchesFilter"/> (Images/Files branches) and the Count* computation in
    /// <see cref="Rebuild"/> call this one helper, so a filter tab and its own count badge can
    /// never disagree about which cards are which (the CLAUDE.md rule against deciding related
    /// fields separately). A stack (2+ paths) is deliberately excluded even when every path is an
    /// image -- per the v1.1 Global Constraints, only a LONE image file reclassifies as 画像; a
    /// stack always stays ファイル.
    /// </summary>
    private static bool IsSingleImageFile(ClipItem item) =>
        item.Kind == ClipKind.Files &&
        item.Paths.Count == 1 &&
        MediaKind.Classify(item.Paths[0]) == MediaCategory.Image;

    private static bool MatchesSearch(ClipItem item, string search)
    {
        if (string.IsNullOrEmpty(search))
        {
            return true;
        }

        // Text/Title/URL are all just item.Text for the kinds that have one (Text and Link
        // store their content there; Title for those kinds is always derived from item.Text).
        if (!string.IsNullOrEmpty(item.Text) &&
            item.Text.Contains(search, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (item.Kind == ClipKind.Files)
        {
            foreach (var path in item.Paths)
            {
                var name = Path.GetFileName(path);
                if (!string.IsNullOrEmpty(name) && name.Contains(search, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // Fall back to the full path too: GetFileName can come back empty for odd
                // inputs, and matching the whole path is strictly more permissive.
                if (path.Contains(search, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
