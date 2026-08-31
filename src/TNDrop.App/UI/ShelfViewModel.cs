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

    /// <summary>Count of items matching the current search across BOTH decks -- searched-unpinned
    /// plus searched-pinned -- so this and <see cref="TotalCount"/> read the same when no
    /// filter/search narrows anything (see <see cref="Rebuild"/>'s computation). Backs the "全て"
    /// filter tab's own badge (v1.1 review fix: this used to count unpinned-searched only, which
    /// under-reported against the footer's <see cref="TotalCount"/> whenever anything was pinned).
    /// Scoped by search, unlike TotalCount, which stays search-independent -- keep that difference
    /// in mind before assuming the two must always match.
    /// <para>ONE-RESOLUTION: this and <see cref="CountText"/>/<see cref="CountLinks"/>/
    /// <see cref="CountImages"/>/<see cref="CountFiles"/> are all five computed from the same
    /// `searchedAll` sequence in <see cref="Rebuild"/> (v1.1 re-review fix: a first pass computed
    /// CountAll from searched-unpinned+searched-pinned but left the four sub-counts reading
    /// searched-unpinned only, which let CountAll silently exceed their sum the moment anything was
    /// pinned). CountAll always equals CountText+CountLinks+CountImages+CountFiles as a result --
    /// do not reintroduce a second, differently-scoped source for any one of these five.</para>
    /// </summary>
    public int CountAll => _countAll;


    public int CountText => _countText;

    public int CountLinks => _countLinks;

    public int CountImages => _countImages;

    public int CountFiles => _countFiles;

    /// <summary>Total FILES in the store, across everything - pinned and unpinned, every kind -
    /// independent of the current filter/search. Backs the footer's "全 {0} 件" (v1.1 Task C).
    /// Read straight off the store rather than cached in a field: Rebuild already re-raises
    /// PropertyChanged for it on every path that could change it (a store change, or the user's
    /// own Filter/SearchText), so there is nothing a cache would buy here.
    /// <para>Deliberately NOT filtered by search (unlike <see cref="CountAll"/>): this is the
    /// footer's "how much exists at all" number, and only equals CountAll once the search box is
    /// empty. Do not change one of these two without checking whether the other still needs to
    /// agree with it in the no-search case.</para>
    /// <para>v1.4 Task A: weighted by <see cref="Contribution"/> -- the SAME per-card weight the
    /// five filter badges use (<see cref="ClipItem.IsStack"/> ? Paths.Count : 1) -- rather than a
    /// flat <c>_store.Items.Count</c>. Before this, grouping N single-file cards into one stack
    /// silently dropped the footer's "全 N 件" from N to 1 while the badges (already file-weighted
    /// since v1.3 Task A) kept reading N, so the footer and the badges disagreed about how many
    /// files existed. Reusing Contribution here instead of re-deriving the same weight a second
    /// way is the one-resolution rule (CLAUDE.md): a stack's file count has exactly one place it
    /// is computed.</para></summary>
    public int TotalCount => _store.Items.Sum(Contribution);

    /// <summary>
    /// Count of cards actually on screen right now: the filtered/searched <see cref="Cards"/>
    /// deck plus the <see cref="PinnedCards"/> deck. Backs the footer's "{0} / 全 {1} 件" (v1.1
    /// Task C).
    /// <para>DECISION (see task-C-brief.md): the pinned deck is included even while a
    /// filter/search would otherwise exclude some of those items, because <see cref="Rebuild"/>
    /// always shows every pinned item regardless of Filter/SearchText - it is genuinely still
    /// visible on screen, so leaving it out of this count would make the footer under-report what
    /// the user can actually see.</para>
    /// <para>KNOWN, OUT-OF-SCOPE SEPARATE SCOPE from <see cref="CountAll"/>: CountAll counts only
    /// pinned items that themselves pass the current search (see its own doc comment), while
    /// PinnedCards -- and therefore VisibleCount -- always includes every pinned item regardless of
    /// search (pre-existing v1.1 design, unchanged here). So VisibleCount can be larger than
    /// CountAll while a search is active and a pinned item does not match it. This is expected, not
    /// a drift to fix.</para>
    /// <para>DECISION (v1.2 Task H, the pinned accordion): this keeps summing every card in both
    /// collections whether the accordion is expanded or COLLAPSED. It is an item count, not
    /// a pixel count -- collapsing the section hides the cards behind one click without removing
    /// them from the shelf, exactly as scrolling the main list past a card does not remove it
    /// either, and neither has ever been subtracted here. Making the footer number jump when a
    /// purely visual section folds would also put this view model in the business of tracking
    /// ShelfWindow's chrome state, which it deliberately does not do (the accordion's open/closed
    /// flag lives in ShelfWindow and AppSettings.PinnedExpanded, not here).</para>
    /// <para>v1.4 Task A: weighted by <see cref="Contribution"/>, the same per-card weight
    /// <see cref="TotalCount"/> and the five filter badges use, rather than a flat
    /// <c>Cards.Count + PinnedCards.Count</c>. This is the ONLY other reader of the raw card
    /// counts besides the footer itself (checked before this change): no other consumer needs a
    /// card-based number here, so the property is reweighted in place rather than adding a
    /// second, differently-scoped file-count property next to it.</para>
    /// </summary>
    public int VisibleCount => Cards.Concat(PinnedCards).Sum(c => Contribution(c.Item));

    /// <summary>True while a filter other than All is active, or the search box has text - i.e.
    /// whichever moment the footer's format should switch from "全 {0} 件" to "{0} / 全 {1} 件".
    /// Single resolution: ShelfWindow reads only this property rather than re-deriving the same
    /// condition from Filter/SearchText itself.</summary>
    public bool IsFilterActive => _filter != CardFilter.All || !string.IsNullOrEmpty(_searchText);

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

    /// <summary>Files that <see cref="ClearVisible"/> would delete right now, weighted by
    /// <see cref="Contribution"/> -- the SAME per-card weight <see cref="TotalCount"/>,
    /// <see cref="VisibleCount"/> and the five filter badges use -- so a 3-file stack in
    /// <see cref="Cards"/> reads as 3, not 1. Deliberately scoped to <see cref="Cards"/> alone,
    /// NOT <c>Cards.Concat(PinnedCards)</c> like <see cref="VisibleCount"/>: <see cref="ClearVisible"/>
    /// only ever removes unpinned items (pinned items are excluded from <see cref="Cards"/> entirely),
    /// so a property meant to describe "how many files this button is about to delete" must match
    /// that scope, not the footer's "how many files are on screen" scope.
    /// <para>v1.4 review fix I1: the clear-confirmation dialog used to format its 「表示中の {0}
    /// 件を削除しますか?」 prompt from a raw <c>Cards.Count</c> (card count), so confirming the
    /// deletion of one 3-file stack showed "1件" when 3 files were actually about to be deleted.
    /// The prompt now reads this property instead -- reusing <see cref="Contribution"/> rather than
    /// adding a second, differently-scoped counting rule (one-resolution rule, CLAUDE.md).</para>
    /// </summary>
    public int ClearVisibleFileCount => Cards.Sum(c => Contribution(c.Item));

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

        // ONE sequence backs EVERY Count* property -- CountAll and all four per-kind sub-counts
        // (one-resolution rule, v1.1 re-review): searched-unpinned UNION searched-pinned. Building
        // CountAll from this union while still deriving the sub-counts from `searched` alone (the
        // first fix attempt) let CountAll silently exceed CountText+CountLinks+CountImages+
        // CountFiles the moment anything was pinned -- the same kind of drift the rule exists to
        // prevent. Every Count* below reads only `searchedAll`, and the per-kind ones reuse the
        // exact same IsImageEntity classification helper MatchesFilter itself uses, so a
        // filter tab and its own badge (and the "全て" tab) can never disagree.
        //
        // NOTE this is a deliberately different scope than VisibleCount (Cards.Count +
        // PinnedCards.Count, see that property's own doc comment): PinnedCards always renders
        // EVERY pinned item regardless of search/filter (pre-existing v1.1 design, out of scope
        // here), so VisibleCount can exceed searchedAll.Count whenever a pinned item does NOT
        // match the current search -- that gap is expected, not a bug to chase.
        var searchedAll = searched.Concat(pinnedItems.Where(i => MatchesSearch(i, _searchText))).ToList();
        // v1.3 Task A: each card's contribution to its one badge is Contribution(item) --
        // IsStack ? Paths.Count : 1 -- not a flat 1 per card, so grouping/splitting files no
        // longer changes the total file count a badge reports. Every Count* below still reads
        // `searchedAll` filtered by the exact same MatchesFilter predicate the tabs themselves
        // use (one-resolution rule), so CountAll == sum of the four sub-counts continues to hold:
        // MatchesFilter partitions searchedAll into four disjoint groups, and summing the same
        // per-item weight function over a partition always equals summing it over the whole.
        _countAll = searchedAll.Sum(Contribution);
        _countText = searchedAll.Where(i => MatchesFilter(i, CardFilter.Text)).Sum(Contribution);
        _countLinks = searchedAll.Where(i => MatchesFilter(i, CardFilter.Links)).Sum(Contribution);
        _countImages = searchedAll.Where(i => MatchesFilter(i, CardFilter.Images)).Sum(Contribution);
        _countFiles = searchedAll.Where(i => MatchesFilter(i, CardFilter.Files)).Sum(Contribution);

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
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(IsFilterActive));

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
        CardFilter.Images => item.Kind == ClipKind.Image || IsImageEntity(item),
        CardFilter.Files => item.Kind == ClipKind.Files && !IsImageEntity(item),
        _ => true,
    };

    /// <summary>
    /// SINGLE RESOLUTION for "how many files does this card contribute to its one badge":
    /// exactly <see cref="ClipItem.Paths"/>'s count for a stack (<see cref="ClipItem.IsStack"/>,
    /// the same check <see cref="CardViewModel.IsStack"/> reads), otherwise 1. Every Count* field
    /// in <see cref="Rebuild"/> sums this over its own MatchesFilter-partitioned slice of
    /// `searchedAll`, so a card is weighted the same way no matter which one of the five badges
    /// it lands in -- there is no second place a badge total gets computed.
    ///
    /// <para>v1.3 Task A: this replaces the earlier flat "1 per card" weight (badges used to be
    /// card counts). Grouping N single-file cards into one stack, or splitting a stack back
    /// apart, changes which cards exist and how many Cards.Count reports (filter membership is
    /// still card-based, unchanged) -- but must NOT change how many files a badge reports, since
    /// no files were created or destroyed. Weighting every item by its own file count, rather
    /// than by 1, is what keeps that total invariant across a merge/split round trip. Reading
    /// <see cref="ClipItem.IsStack"/> (which itself always reads live off <c>Paths.Count</c>,
    /// never a cached flag) rather than re-testing Paths.Count here is what keeps this correct
    /// even when a stack shrinks by one path but stays a stack (SplitFile mutates Paths in place
    /// on the same ClipItem instance).</para>
    ///
    /// <para>v1.8: the same invariant now also applies to a text stack (<see
    /// cref="ClipItem.IsTextStack"/>) -- weighted by <see cref="ClipItem.Texts"/>.Count so a
    /// merge/split of text entries leaves the テキスト badge's total unchanged too.</para>
    /// </summary>
    private static int Contribution(ClipItem item) =>
        item.IsStack ? item.Paths.Count
        : item.IsTextStack ? item.Texts.Count
        : 1;

    /// <summary>
    /// SINGLE RESOLUTION for "does this item count as 画像 instead of ファイル": both
    /// <see cref="MatchesFilter"/> (Images/Files branches) and the Count* computation in
    /// <see cref="Rebuild"/> call this one helper, so a filter tab and its own count badge can
    /// never disagree about which cards are which (the CLAUDE.md rule against deciding related
    /// fields separately).
    ///
    /// <para>v1.2 Task A widening: a lone image file still counts (unchanged from v1.1's
    /// IsSingleImageFile), and now so does a STACK (2+ paths) whose every path classifies as
    /// <see cref="MediaCategory.Image"/> -- a video or a non-media file anywhere in the stack
    /// flips the whole thing back to ファイル. This deliberately reverses the v1.1 Global
    /// Constraints decision that a stack always stays ファイル; see the v1.2 plan's decision (1).
    /// Note this is a DIFFERENT question than <see cref="CardViewModel.Media"/>/
    /// <see cref="CardViewModel.IsMediaFile"/>, which stay Other/false for every stack regardless
    /// of its contents -- those two answer "does this card show a large shell thumbnail in place
    /// of its icon", not "which filter tab counts this card", and a stack's large-thumbnail
    /// eligibility is instead driven by <see cref="CardViewModel.StackThumbnail"/>.</para>
    /// </summary>
    private static bool IsImageEntity(ClipItem item)
    {
        if (item.Kind != ClipKind.Files || item.Paths.Count == 0)
        {
            return false;
        }

        if (item.Paths.Count == 1)
        {
            return MediaKind.Classify(item.Paths[0]) == MediaCategory.Image;
        }

        // Stack: every path must classify as Image -- a single video or non-media path anywhere
        // in the stack is enough to keep the whole card as ファイル.
        foreach (var path in item.Paths)
        {
            if (MediaKind.Classify(path) != MediaCategory.Image)
            {
                return false;
            }
        }

        return true;
    }

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

        // v1.8: テキストスタックの中身にもヒットさせる (ヒットしたらスタックカードが出る)。
        foreach (var text in item.Texts)
        {
            if (text.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
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
