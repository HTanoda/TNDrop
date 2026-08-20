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

    /// <summary>Deletes every unpinned item matching the current filter+search -- i.e. exactly
    /// what's in <see cref="Cards"/> right now. Caller (ShelfWindow) is responsible for
    /// confirming with the user first.</summary>
    public void ClearVisible()
    {
        var ids = Cards.Select(c => c.Id).ToList();
        if (ids.Count == 0)
        {
            return;
        }

        _store.RemoveMany(ids);
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
        _countImages = searched.Count(i => i.Kind == ClipKind.Image);
        _countFiles = searched.Count(i => i.Kind == ClipKind.Files);

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
        CardFilter.Images => item.Kind == ClipKind.Image,
        CardFilter.Files => item.Kind == ClipKind.Files,
        _ => true,
    };

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
