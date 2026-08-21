using System;
using System.IO;
using TNDrop.Core;
using TNDrop.UI;

public class ShelfViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tndrop-test-" + Guid.NewGuid());
    private readonly ItemStore _store;
    public ShelfViewModelTests() { _store = new ItemStore(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private void Add(ClipKind kind, string content)
    {
        var item = kind == ClipKind.Files
            ? ItemStore.BuildFileItems(new[] { content }, DateTime.UtcNow)[0]
            : new ClipItem { Kind = kind, Text = content, CreatedAtUtc = DateTime.UtcNow,
                             ContentHash = ItemStore.Fnv1a(System.Text.Encoding.UTF8.GetBytes(content)) };
        _store.TryAdd(item);
    }

    [StaFact]
    public void Filter_and_search_narrow_cards()
    {
        Add(ClipKind.Text, "会議メモ");
        Add(ClipKind.Link, "https://example.com/page");
        Add(ClipKind.Files, @"C:\docs\申請書.xlsx");
        var vm = new ShelfViewModel(_store);
        Assert.Equal(3, vm.Cards.Count);
        vm.Filter = CardFilter.Links;
        Assert.Single(vm.Cards);
        vm.Filter = CardFilter.All;
        vm.SearchText = "申請";
        Assert.Single(vm.Cards);
        Assert.Equal(ClipKind.Files, vm.Cards[0].Kind);
    }

    [StaFact]
    public void Pinned_items_go_to_pinned_deck()
    {
        Add(ClipKind.Text, "a");
        _store.SetPinned(_store.Items[0].Id, true);
        var vm = new ShelfViewModel(_store);
        Assert.Empty(vm.Cards);
        Assert.Single(vm.PinnedCards);
    }

    // -- v1.1 Task B / v1.2 Task A: unified image classification -----------------------------
    //
    // A lone image-extension file counts as 画像 (Images), not ファイル (Files); a lone video
    // stays ファイル. v1.2 widens the stack case (reversing v1.1's "a stack always stays
    // ファイル"): a stack (2+ paths) counts as 画像 only when EVERY path is an image extension --
    // a video or any other non-image path anywhere in the stack keeps it as ファイル.
    // MatchesFilter and the Count* fields must agree on all of these, since they are computed
    // from the same helper (one-resolution rule) -- see ShelfViewModel.IsImageEntity.

    [StaFact]
    public void Single_image_file_counts_as_Images_not_Files()
    {
        Add(ClipKind.Files, @"C:\pics\photo.png");
        var vm = new ShelfViewModel(_store);

        Assert.Equal(1, vm.CountImages);
        Assert.Equal(0, vm.CountFiles);

        vm.Filter = CardFilter.Images;
        Assert.Single(vm.Cards);

        vm.Filter = CardFilter.Files;
        Assert.Empty(vm.Cards);
    }

    [StaFact]
    public void Single_video_file_counts_as_Files_not_Images()
    {
        Add(ClipKind.Files, @"C:\mov\clip.mp4");
        var vm = new ShelfViewModel(_store);

        Assert.Equal(0, vm.CountImages);
        Assert.Equal(1, vm.CountFiles);

        vm.Filter = CardFilter.Files;
        Assert.Single(vm.Cards);

        vm.Filter = CardFilter.Images;
        Assert.Empty(vm.Cards);
    }

    [StaFact]
    public void All_image_two_file_stack_counts_as_Images_not_Files()
    {
        var paths = new[] { @"C:\pics\a.png", @"C:\pics\b.png" };
        _store.TryAdd(ItemStore.BuildFileItems(paths, DateTime.UtcNow)[0]);
        var vm = new ShelfViewModel(_store);

        Assert.Equal(1, vm.CountImages);
        Assert.Equal(0, vm.CountFiles);

        vm.Filter = CardFilter.Images;
        Assert.Single(vm.Cards);

        vm.Filter = CardFilter.Files;
        Assert.Empty(vm.Cards);
    }

    [StaFact]
    public void All_image_three_file_stack_counts_as_Images_not_Files()
    {
        var paths = new[] { @"C:\pics\a.png", @"C:\pics\b.jpg", @"C:\pics\c.webp" };
        _store.TryAdd(ItemStore.BuildFileItems(paths, DateTime.UtcNow)[0]);
        var vm = new ShelfViewModel(_store);

        Assert.Equal(1, vm.CountImages);
        Assert.Equal(0, vm.CountFiles);

        vm.Filter = CardFilter.Images;
        Assert.Single(vm.Cards);

        vm.Filter = CardFilter.Files;
        Assert.Empty(vm.Cards);
    }

    [StaFact]
    public void Image_and_text_mixed_stack_counts_as_Files_not_Images()
    {
        var paths = new[] { @"C:\pics\a.png", @"C:\docs\b.txt" };
        _store.TryAdd(ItemStore.BuildFileItems(paths, DateTime.UtcNow)[0]);
        var vm = new ShelfViewModel(_store);

        Assert.Equal(0, vm.CountImages);
        Assert.Equal(1, vm.CountFiles);

        vm.Filter = CardFilter.Files;
        Assert.Single(vm.Cards);

        vm.Filter = CardFilter.Images;
        Assert.Empty(vm.Cards);
    }

    [StaFact]
    public void Image_and_video_mixed_stack_counts_as_Files_not_Images()
    {
        var paths = new[] { @"C:\pics\a.png", @"C:\mov\b.mp4" };
        _store.TryAdd(ItemStore.BuildFileItems(paths, DateTime.UtcNow)[0]);
        var vm = new ShelfViewModel(_store);

        Assert.Equal(0, vm.CountImages);
        Assert.Equal(1, vm.CountFiles);

        vm.Filter = CardFilter.Files;
        Assert.Single(vm.Cards);

        vm.Filter = CardFilter.Images;
        Assert.Empty(vm.Cards);
    }

    [StaFact]
    public void Kind_Image_blob_still_counts_as_Images_unchanged()
    {
        var item = new ClipItem
        {
            Kind = ClipKind.Image,
            CreatedAtUtc = DateTime.UtcNow,
            ContentHash = 1,
        };
        _store.TryAdd(item);
        var vm = new ShelfViewModel(_store);

        Assert.Equal(1, vm.CountImages);
        Assert.Equal(0, vm.CountFiles);
    }

    // -- v1.1 Task C: footer count (全 {0} 件 / {0} / 全 {1} 件) --------------------------------
    //
    // TotalCount is the store's whole item count (pinned + unpinned, every kind) - independent of
    // the current filter/search. VisibleCount is what is actually on screen right now: the
    // filtered/searched Cards deck plus the PinnedCards deck, which always shows regardless of
    // filter/search (see ShelfViewModel.Rebuild's own comment on that). Both single-resolution:
    // ShelfWindow's footer reads only these two properties, never recomputing the same counts a
    // second way.

    [StaFact]
    public void TotalCount_is_the_whole_store_including_pinned()
    {
        Add(ClipKind.Text, "a");
        Add(ClipKind.Link, "https://example.com/page");
        _store.SetPinned(_store.Items[0].Id, true);
        var vm = new ShelfViewModel(_store);

        Assert.Equal(2, vm.TotalCount);

        vm.Filter = CardFilter.Links;
        Assert.Equal(2, vm.TotalCount);
    }

    [StaFact]
    public void VisibleCount_sums_filtered_cards_and_the_pinned_deck()
    {
        Add(ClipKind.Text, "hello");
        Add(ClipKind.Link, "https://example.com/page");
        Add(ClipKind.Files, @"C:\docs\a.txt");
        _store.SetPinned(_store.Items[0].Id, true); // pins the just-added Files item

        var vm = new ShelfViewModel(_store);

        // All filter: 2 unpinned cards (text + link) + 1 pinned (files) = 3.
        Assert.Equal(3, vm.VisibleCount);
        Assert.Equal(3, vm.TotalCount);

        vm.Filter = CardFilter.Links;

        // Only the link card matches the filter among the unpinned deck; the pinned deck is
        // unaffected by the filter and still counts.
        Assert.Equal(2, vm.VisibleCount);
        Assert.Equal(3, vm.TotalCount);
    }

    // -- v1.2 Task H: VisibleCount is unaffected by the pinned accordion ------------------------
    //
    // The accordion can hide every pinned card behind one click. DECISION (see VisibleCount's own
    // doc comment): the footer number does NOT change when it does -- VisibleCount is an item
    // count, and the collapsed cards are still on the shelf. This is locked here as the property
    // that makes it structurally true: VisibleCount is derived from the two collections and
    // nothing else, so this view model has no way to observe the accordion in the first place.
    [StaFact]
    public void VisibleCount_is_exactly_the_two_card_collections_and_knows_nothing_of_the_accordion()
    {
        Add(ClipKind.Text, "hello");
        Add(ClipKind.Link, "https://example.com/page");
        Add(ClipKind.Files, @"C:\docs\a.txt");
        _store.SetPinned(_store.Items[0].Id, true);

        var vm = new ShelfViewModel(_store);
        Assert.Equal(vm.Cards.Count + vm.PinnedCards.Count, vm.VisibleCount);

        vm.Filter = CardFilter.Links;
        Assert.Equal(vm.Cards.Count + vm.PinnedCards.Count, vm.VisibleCount);

        vm.SearchText = "nothing matches this";
        Assert.Equal(vm.Cards.Count + vm.PinnedCards.Count, vm.VisibleCount);
    }

    // -- v1.1 final fix wave: CountAll vs TotalCount agreement when items are pinned ----------
    //
    // CountAll (the "全て" filter tab's own badge) used to count unpinned-searched items only, so
    // it under-reported against the footer's TotalCount whenever anything was pinned. Fixed to
    // count searched-unpinned + searched-pinned (see ShelfViewModel.CountAll's own doc comment).

    [StaFact]
    public void CountAll_matches_TotalCount_when_nothing_is_pinned_or_searched()
    {
        Add(ClipKind.Text, "a");
        Add(ClipKind.Link, "https://example.com/page");
        var vm = new ShelfViewModel(_store);

        Assert.Equal(2, vm.CountAll);
        Assert.Equal(2, vm.TotalCount);
    }

    [StaFact]
    public void CountAll_includes_pinned_items_that_pass_the_search()
    {
        Add(ClipKind.Text, "hello");
        Add(ClipKind.Link, "https://example.com/page");
        Add(ClipKind.Files, @"C:\docs\a.txt");
        _store.SetPinned(_store.Items[0].Id, true); // pins the just-added Files item

        var vm = new ShelfViewModel(_store);

        // No search: every item (pinned or not) counts, matching TotalCount exactly -- this is
        // the disagreement the v1.1 review caught (CountAll used to read 2 here, not 3).
        Assert.Equal(3, vm.CountAll);
        Assert.Equal(vm.TotalCount, vm.CountAll);
        AssertCountAllEqualsSumOfSubcounts(vm);

        // A search that only the pinned Files item matches: CountAll must still count it even
        // though it lives in PinnedCards, not Cards.
        vm.SearchText = "a.txt";
        Assert.Equal(1, vm.CountAll);
        AssertCountAllEqualsSumOfSubcounts(vm);
    }

    // -- v1.1 re-review: CountAll and the four per-kind sub-counts must share one scope ----------
    //
    // A first fix pass computed CountAll from searched-unpinned+searched-pinned but left
    // CountText/CountLinks/CountImages/CountFiles reading searched-unpinned only, which let
    // CountAll silently exceed their sum (a visible "全て" > sum-of-tabs inconsistency) the moment
    // anything was pinned. Rebuild now derives all five from one `searchedAll` sequence.

    [StaFact]
    public void CountAll_equals_sum_of_subcounts_with_a_pinned_text_item()
    {
        Add(ClipKind.Text, "pinned note");
        _store.SetPinned(_store.Items[0].Id, true);

        var vm = new ShelfViewModel(_store);

        // The pinned text item lives only in PinnedCards, not Cards -- CountAll (and CountText)
        // must still include it.
        Assert.Equal(1, vm.CountAll);
        Assert.Equal(1, vm.CountText);
        Assert.Equal(0, vm.CountLinks);
        Assert.Equal(0, vm.CountImages);
        Assert.Equal(0, vm.CountFiles);
        AssertCountAllEqualsSumOfSubcounts(vm);
    }

    [StaFact]
    public void CountAll_equals_sum_of_subcounts_with_a_mix_of_pinned_and_unpinned_items()
    {
        Add(ClipKind.Text, "a");
        Add(ClipKind.Link, "https://example.com/page");
        Add(ClipKind.Files, @"C:\pics\photo.png"); // single image file -> counts as Images
        _store.SetPinned(_store.Items[0].Id, true); // pins the just-added Files/Image item

        var vm = new ShelfViewModel(_store);

        Assert.Equal(3, vm.CountAll);
        Assert.Equal(1, vm.CountText);
        Assert.Equal(1, vm.CountLinks);
        Assert.Equal(1, vm.CountImages);
        Assert.Equal(0, vm.CountFiles);
        AssertCountAllEqualsSumOfSubcounts(vm);
    }

    // -- v1.2 Task A: CountAll vs sub-counts with an all-image stack, pinned and unpinned --------
    //
    // IsImageEntity's stack-widening must not break the CountAll==sum-of-subcounts invariant in
    // either pinned scope, since Rebuild derives CountAll and the four per-kind counts from the
    // same searchedAll sequence regardless of which deck (Cards vs PinnedCards) an item ends up in.

    [StaFact]
    public void CountAll_equals_sum_of_subcounts_with_an_all_image_stack_unpinned()
    {
        var paths = new[] { @"C:\pics\a.png", @"C:\pics\b.jpg", @"C:\pics\c.webp" };
        _store.TryAdd(ItemStore.BuildFileItems(paths, DateTime.UtcNow)[0]);
        Add(ClipKind.Text, "a");
        Add(ClipKind.Link, "https://example.com/page");

        var vm = new ShelfViewModel(_store);

        Assert.Equal(3, vm.CountAll);
        Assert.Equal(1, vm.CountText);
        Assert.Equal(1, vm.CountLinks);
        Assert.Equal(1, vm.CountImages);
        Assert.Equal(0, vm.CountFiles);
        AssertCountAllEqualsSumOfSubcounts(vm);
    }

    [StaFact]
    public void CountAll_equals_sum_of_subcounts_with_an_all_image_stack_pinned()
    {
        var paths = new[] { @"C:\pics\a.png", @"C:\pics\b.jpg", @"C:\pics\c.webp" };
        var stackItem = ItemStore.BuildFileItems(paths, DateTime.UtcNow)[0];
        _store.TryAdd(stackItem);
        _store.SetPinned(stackItem.Id, true);
        Add(ClipKind.Text, "a");
        Add(ClipKind.Link, "https://example.com/page");

        var vm = new ShelfViewModel(_store);

        // The pinned all-image stack lives only in PinnedCards, not Cards -- CountAll (and
        // CountImages) must still include it, same as the pre-existing pinned-text-item test above.
        Assert.Equal(3, vm.CountAll);
        Assert.Equal(1, vm.CountText);
        Assert.Equal(1, vm.CountLinks);
        Assert.Equal(1, vm.CountImages);
        Assert.Equal(0, vm.CountFiles);
        AssertCountAllEqualsSumOfSubcounts(vm);
    }

    private static void AssertCountAllEqualsSumOfSubcounts(ShelfViewModel vm) =>
        Assert.Equal(vm.CountAll, vm.CountText + vm.CountLinks + vm.CountImages + vm.CountFiles);
}
