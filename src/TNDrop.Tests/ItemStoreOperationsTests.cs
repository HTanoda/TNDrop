using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TNDrop.Core;

public class ItemStoreOperationsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tndrop-test-" + Guid.NewGuid());
    private readonly ItemStore _store;
    public ItemStoreOperationsTests() { _store = new ItemStore(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static ClipItem Text(string t) => new()
    {
        Kind = ClipKind.Text, Text = t, CreatedAtUtc = DateTime.UtcNow,
        ContentHash = ItemStore.Fnv1a(System.Text.Encoding.UTF8.GetBytes(t))
    };

    [Fact]
    public void TryAdd_rejects_consecutive_duplicate()
    {
        Assert.True(_store.TryAdd(Text("a")));
        Assert.False(_store.TryAdd(Text("a")));   // 直前と同一
        Assert.True(_store.TryAdd(Text("b")));
        Assert.True(_store.TryAdd(Text("a")));    // 直前が違えば再追加可
        Assert.Equal(3, _store.Items.Count);
        Assert.Equal("a", _store.Items[0].Text);  // 先頭が最新
    }

    [Fact]
    public void BuildFileItems_chunks_by_ten()
    {
        var paths = Enumerable.Range(1, 23).Select(i => $@"C:\f\{i}.txt").ToList();
        var items = ItemStore.BuildFileItems(paths, DateTime.UtcNow);
        Assert.Equal(3, items.Count);
        Assert.Equal(10, items[0].Paths.Count);
        Assert.Equal(3, items[2].Paths.Count);
        Assert.All(items, i => Assert.Equal(ClipKind.Files, i.Kind));
    }

    [Fact]
    public void TryMergeFiles_merges_within_cap_and_removes_source()
    {
        var a = ItemStore.BuildFileItems(new[] { @"C:\a1", @"C:\a2" }, DateTime.UtcNow)[0];
        var b = ItemStore.BuildFileItems(new[] { @"C:\b1", @"C:\a2" }, DateTime.UtcNow)[0];
        _store.TryAdd(a); _store.TryAdd(b);
        Assert.True(_store.TryMergeFiles(a.Id, b.Id));
        Assert.Single(_store.Items);
        Assert.Equal(3, _store.Items[0].Paths.Count); // a1,a2,b1 (a2 重複除外)
    }

    [Fact]
    public void TryMergeFiles_keeps_a_pinned_source_protected()
    {
        // The merge DELETES the source item. Without OR-ing the flag, dragging a pinned card onto
        // an unpinned one would hand the user's protected paths to a card PurgeOlderThan is free
        // to delete -- the pin would be silently revoked by a gesture that never mentions pinning.
        var target = ItemStore.BuildFileItems(new[] { @"C:\t1" }, DateTime.UtcNow)[0];
        var source = ItemStore.BuildFileItems(new[] { @"C:\s1" }, DateTime.UtcNow)[0];
        source.Pinned = true;

        _store.TryAdd(target); _store.TryAdd(source);
        Assert.True(_store.TryMergeFiles(target.Id, source.Id));

        var merged = _store.Items.Single();
        Assert.Equal(target.Id, merged.Id);
        Assert.True(merged.Pinned);
        Assert.Equal(new[] { @"C:\t1", @"C:\s1" }, merged.Paths);
    }

    [Fact]
    public void TryMergeFiles_leaves_an_unpinned_source_unpinned()
    {
        // The contrast case: OR-ing must not turn every merge into a pin.
        var target = ItemStore.BuildFileItems(new[] { @"C:\t1" }, DateTime.UtcNow)[0];
        var source = ItemStore.BuildFileItems(new[] { @"C:\s1" }, DateTime.UtcNow)[0];

        _store.TryAdd(target); _store.TryAdd(source);
        Assert.True(_store.TryMergeFiles(target.Id, source.Id));
        Assert.False(_store.Items.Single().Pinned);
    }

    [Fact]
    public void TryMergeFiles_keeps_a_pinned_target_pinned()
    {
        var target = ItemStore.BuildFileItems(new[] { @"C:\t1" }, DateTime.UtcNow)[0];
        target.Pinned = true;
        var source = ItemStore.BuildFileItems(new[] { @"C:\s1" }, DateTime.UtcNow)[0];

        _store.TryAdd(target); _store.TryAdd(source);
        Assert.True(_store.TryMergeFiles(target.Id, source.Id));
        Assert.True(_store.Items.Single().Pinned);
    }

    [Fact]
    public void TryMergeFiles_fails_when_over_cap()
    {
        var a = ItemStore.BuildFileItems(Enumerable.Range(1, 9).Select(i => $@"C:\a{i}").ToArray(), DateTime.UtcNow)[0];
        var b = ItemStore.BuildFileItems(new[] { @"C:\b1", @"C:\b2" }, DateTime.UtcNow)[0];
        _store.TryAdd(a); _store.TryAdd(b);
        Assert.False(_store.TryMergeFiles(a.Id, b.Id)); // 9+2 > 10
        Assert.Equal(2, _store.Items.Count);            // 変更なし
    }

    [Fact]
    public void SplitFile_extracts_single_file_card()
    {
        var s = ItemStore.BuildFileItems(new[] { @"C:\x", @"C:\y", @"C:\z" }, DateTime.UtcNow)[0];
        _store.TryAdd(s);
        var card = _store.SplitFile(s.Id, @"C:\y");
        Assert.NotNull(card);
        Assert.Equal(new[] { @"C:\y" }, card!.Paths);
        Assert.Equal(new[] { @"C:\x", @"C:\z" }, _store.Items.First(i => i.Id == s.Id).Paths);
    }

    [Fact]
    public void SplitFile_from_pinned_stack_yields_pinned_card()
    {
        // The extracted card must inherit Pinned from the source stack, mirroring TryMergeFiles'
        // OR-in-the-pin principle: with PurgeUnpinnedOnRestart, an unpinned extracted card would
        // be silently deleted on the next restart even though the user pinned that path.
        var s = ItemStore.BuildFileItems(new[] { @"C:\x", @"C:\y", @"C:\z" }, DateTime.UtcNow)[0];
        s.Pinned = true;
        _store.TryAdd(s);
        var card = _store.SplitFile(s.Id, @"C:\y");
        Assert.NotNull(card);
        Assert.True(card!.Pinned);
    }

    [Fact]
    public void SplitFile_from_unpinned_stack_yields_unpinned_card()
    {
        var s = ItemStore.BuildFileItems(new[] { @"C:\x", @"C:\y", @"C:\z" }, DateTime.UtcNow)[0];
        _store.TryAdd(s);
        var card = _store.SplitFile(s.Id, @"C:\y");
        Assert.NotNull(card);
        Assert.False(card!.Pinned);
    }

    [Fact]
    public void PurgeOlderThan_skips_pinned_and_deletes_blobs()
    {
        Directory.CreateDirectory(_store.BlobsDir);
        var blob = Path.Combine(_store.BlobsDir, "img1.png");
        File.WriteAllBytes(blob, new byte[] { 1 });
        var old1 = Text("old"); old1.CreatedAtUtc = DateTime.UtcNow.AddDays(-2);
        var old2 = new ClipItem { Kind = ClipKind.Image, ImageFile = "img1.png",
            CreatedAtUtc = DateTime.UtcNow.AddDays(-2), ContentHash = 1 };
        var pinnedOld = Text("keep"); pinnedOld.CreatedAtUtc = DateTime.UtcNow.AddDays(-2); pinnedOld.Pinned = true;
        _store.TryAdd(old1); _store.TryAdd(old2); _store.TryAdd(pinnedOld);
        var n = _store.PurgeOlderThan(TimeSpan.FromHours(24));
        Assert.Equal(2, n);
        Assert.Single(_store.Items);
        Assert.True(_store.Items[0].Pinned);
        Assert.False(File.Exists(blob));
    }

    private (ClipItem Item, string ImagePath, string ThumbPath) AddImageWithBlobs(string tag)
    {
        Directory.CreateDirectory(_store.BlobsDir);
        var imageFile = $"{tag}-full.png";
        var thumbFile = $"{tag}-thumb.png";
        var imagePath = Path.Combine(_store.BlobsDir, imageFile);
        var thumbPath = Path.Combine(_store.BlobsDir, thumbFile);
        File.WriteAllBytes(imagePath, new byte[] { 1 });
        File.WriteAllBytes(thumbPath, new byte[] { 2 });

        var item = new ClipItem
        {
            Kind = ClipKind.Image,
            ImageFile = imageFile,
            ThumbFile = thumbFile,
            CreatedAtUtc = DateTime.UtcNow,
            ContentHash = ItemStore.Fnv1a(System.Text.Encoding.UTF8.GetBytes(tag)),
        };
        _store.TryAdd(item);
        return (item, imagePath, thumbPath);
    }

    [Fact]
    public void Remove_deletes_image_blobs()
    {
        var (item, imagePath, thumbPath) = AddImageWithBlobs("remove");

        _store.Remove(item.Id);

        Assert.Empty(_store.Items);
        Assert.False(File.Exists(imagePath));
        Assert.False(File.Exists(thumbPath));
    }

    [Fact]
    public void RemoveMany_deletes_image_blobs()
    {
        var (item, imagePath, thumbPath) = AddImageWithBlobs("removemany");

        _store.RemoveMany(new[] { item.Id });

        Assert.Empty(_store.Items);
        Assert.False(File.Exists(imagePath));
        Assert.False(File.Exists(thumbPath));
    }

    [Fact]
    public void RemoveAll_deletes_image_blobs()
    {
        var (item, imagePath, thumbPath) = AddImageWithBlobs("removeall");

        _store.RemoveAll(i => i.Id == item.Id);

        Assert.Empty(_store.Items);
        Assert.False(File.Exists(imagePath));
        Assert.False(File.Exists(thumbPath));
    }

    // v1.2 Task E: App.OnStartup's PurgeUnpinnedOnRestart step is exactly
    // `Store.RemoveAll(i => !i.Pinned)` -- App itself can't be unit-tested (it needs a live WPF
    // Application), so this pins the predicate it relies on: pinned items (of any kind) survive
    // untouched, unpinned items (including an unpinned Image with real blob files on disk) are
    // gone, and their blobs are deleted through the same DeleteBlobsFor path as every other
    // removal.
    [Fact]
    public void RemoveAll_unpinned_predicate_matches_the_restart_purge_and_deletes_blobs()
    {
        var pinnedText = Text("keep-me");
        _store.TryAdd(pinnedText);
        _store.SetPinned(pinnedText.Id, true);

        _store.TryAdd(Text("unpinned-text"));
        var (droppedImage, imagePath, thumbPath) = AddImageWithBlobs("restart-purge-image");
        Assert.False(droppedImage.Pinned);

        _store.RemoveAll(i => !i.Pinned);

        var remaining = _store.Items;
        Assert.Single(remaining);
        Assert.Equal(pinnedText.Id, remaining[0].Id);
        Assert.True(remaining[0].Pinned);
        Assert.False(File.Exists(imagePath));
        Assert.False(File.Exists(thumbPath));
    }

    // ---- TrimUnpinnedToCapacity (v1.2 Task E) --------------------------------------------------

    [Fact]
    public void TrimUnpinnedToCapacity_under_capacity_removes_nothing()
    {
        _store.TryAdd(Text("a")); _store.TryAdd(Text("b")); _store.TryAdd(Text("c"));

        var removed = _store.TrimUnpinnedToCapacity(5);

        Assert.Equal(0, removed);
        Assert.Equal(3, _store.Items.Count);
    }

    [Fact]
    public void TrimUnpinnedToCapacity_at_capacity_removes_nothing()
    {
        _store.TryAdd(Text("a")); _store.TryAdd(Text("b")); _store.TryAdd(Text("c"));

        var removed = _store.TrimUnpinnedToCapacity(3);

        Assert.Equal(0, removed);
        Assert.Equal(3, _store.Items.Count);
    }

    [Fact]
    public void TrimUnpinnedToCapacity_over_capacity_removes_oldest_and_keeps_newest()
    {
        // TryAdd inserts at head, so "d" (added last) is newest -- Items[0].
        _store.TryAdd(Text("a")); _store.TryAdd(Text("b"));
        _store.TryAdd(Text("c")); _store.TryAdd(Text("d"));

        var removed = _store.TrimUnpinnedToCapacity(2);

        Assert.Equal(2, removed);
        Assert.Equal(2, _store.Items.Count);
        Assert.Equal(new[] { "d", "c" }, _store.Items.Select(i => i.Text));
    }

    [Fact]
    public void TrimUnpinnedToCapacity_excludes_pinned_from_the_count_and_never_removes_them()
    {
        var oldest = Text("oldest");
        var pinned = Text("pinned");
        var newer1 = Text("newer1");
        var newer2 = Text("newer2");

        _store.TryAdd(oldest);
        _store.TryAdd(pinned);
        _store.SetPinned(pinned.Id, true);
        _store.TryAdd(newer1);
        _store.TryAdd(newer2);

        // Newest-first: newer2, newer1, pinned, oldest. Capacity 2 counts only unpinned items, so
        // newer2/newer1 are kept, "oldest" is the one excess unpinned item, and "pinned" survives
        // untouched even though it sits between the kept and removed unpinned items.
        var removed = _store.TrimUnpinnedToCapacity(2);

        Assert.Equal(1, removed);
        var remaining = _store.Items.Select(i => i.Text).ToList();
        Assert.DoesNotContain("oldest", remaining);
        Assert.Contains("pinned", remaining);
        Assert.Contains("newer1", remaining);
        Assert.Contains("newer2", remaining);
    }

    [Fact]
    public void TrimUnpinnedToCapacity_deletes_blobs_for_removed_images()
    {
        var (dropped, droppedImagePath, droppedThumbPath) = AddImageWithBlobs("dropped");
        var (kept, _, _) = AddImageWithBlobs("kept");

        // "dropped" was added first (older); "kept" second (newer) -- Items[0] is kept.
        var removed = _store.TrimUnpinnedToCapacity(1);

        Assert.Equal(1, removed);
        Assert.Single(_store.Items);
        Assert.Equal(kept.Id, _store.Items[0].Id);
        Assert.False(File.Exists(droppedImagePath));
        Assert.False(File.Exists(droppedThumbPath));
    }

    [Fact]
    public void TrimUnpinnedToCapacity_zero_capacity_removes_all_unpinned()
    {
        var pinned = Text("pinned");
        _store.TryAdd(pinned);
        _store.SetPinned(pinned.Id, true);
        _store.TryAdd(Text("a")); _store.TryAdd(Text("b"));

        var removed = _store.TrimUnpinnedToCapacity(0);

        Assert.Equal(2, removed);
        Assert.Single(_store.Items);
        Assert.True(_store.Items[0].Pinned);
    }

    // ---- ConvertImageToFileCard (v1.3 Task B: image cards can join a merge) -------------------

    [Fact]
    public void ConvertImageToFileCard_converts_kind_and_paths_and_clears_image_fields()
    {
        var (item, imagePath, _) = AddImageWithBlobs("convert");

        var converted = _store.ConvertImageToFileCard(item.Id, imagePath);

        Assert.NotNull(converted);
        Assert.Equal(ClipKind.Files, converted!.Kind);
        Assert.Equal(new[] { imagePath }, converted.Paths);
        Assert.Null(converted.ImageFile);
        Assert.Null(converted.ThumbFile);

        // Same instance mutated in place -- ItemStore.Items must report the identical change, not
        // just the return value.
        var stored = _store.Items.Single(i => i.Id == item.Id);
        Assert.Equal(ClipKind.Files, stored.Kind);
        Assert.Equal(new[] { imagePath }, stored.Paths);
    }

    [Fact]
    public void ConvertImageToFileCard_preserves_pin_and_created_at()
    {
        var (item, imagePath, _) = AddImageWithBlobs("pin-preserve");
        _store.SetPinned(item.Id, true);
        var createdAt = _store.Items.Single(i => i.Id == item.Id).CreatedAtUtc;

        var converted = _store.ConvertImageToFileCard(item.Id, imagePath);

        Assert.NotNull(converted);
        Assert.True(converted!.Pinned);
        Assert.Equal(createdAt, converted.CreatedAtUtc);
        Assert.Equal(item.Id, converted.Id);
    }

    [Fact]
    public void ConvertImageToFileCard_deletes_the_now_orphaned_thumbnail_but_keeps_the_full_image()
    {
        // Paths now points at the full-size blob, so it must survive; ThumbFile is cleared and no
        // longer reachable from anywhere (Kind==Files never reads ThumbFile), so leaving the thumb
        // blob on disk would be a permanent leak -- it must be deleted right here.
        var (item, imagePath, thumbPath) = AddImageWithBlobs("thumb-cleanup");

        _store.ConvertImageToFileCard(item.Id, imagePath);

        Assert.True(File.Exists(imagePath));
        Assert.False(File.Exists(thumbPath));
    }

    [Fact]
    public void ConvertImageToFileCard_returns_null_for_a_non_image_item_and_changes_nothing()
    {
        var text = Text("plain");
        _store.TryAdd(text);

        var converted = _store.ConvertImageToFileCard(text.Id, @"C:\some\path.png");

        Assert.Null(converted);
        Assert.Equal(ClipKind.Text, _store.Items.Single(i => i.Id == text.Id).Kind);
    }

    [Fact]
    public void ConvertImageToFileCard_returns_null_for_an_unknown_id()
    {
        Assert.Null(_store.ConvertImageToFileCard("no-such-id", @"C:\some\path.png"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ConvertImageToFileCard_returns_null_and_changes_nothing_when_no_path_was_resolved(string? path)
    {
        // Mirrors DragDropSource's own "nothing to drag" refusal for a blob that is missing or
        // undecodable -- the caller passes null/empty when its own resolution came up empty.
        var (item, _, _) = AddImageWithBlobs("no-path");

        var converted = _store.ConvertImageToFileCard(item.Id, path);

        Assert.Null(converted);
        Assert.Equal(ClipKind.Image, _store.Items.Single(i => i.Id == item.Id).Kind);
    }

    [Fact]
    public void ConvertImageToFileCard_then_TryMergeFiles_two_images_yield_a_two_file_stack()
    {
        var (a, aPath, _) = AddImageWithBlobs("merge-a");
        var (b, bPath, _) = AddImageWithBlobs("merge-b");

        Assert.NotNull(_store.ConvertImageToFileCard(a.Id, aPath));
        Assert.NotNull(_store.ConvertImageToFileCard(b.Id, bPath));
        Assert.True(_store.TryMergeFiles(a.Id, b.Id));

        var merged = _store.Items.Single();
        Assert.Equal(ClipKind.Files, merged.Kind);
        Assert.Equal(new[] { aPath, bPath }, merged.Paths);
        Assert.True(merged.IsStack);
        Assert.All(merged.Paths, p => Assert.StartsWith(_store.BlobsDir, p, StringComparison.OrdinalIgnoreCase));
    }

    // ---- blob cleanup for Files cards that reference blobs\ (v1.3 Task B) ----------------------

    [Fact]
    public void Remove_deletes_a_blobs_dir_path_referenced_by_a_converted_Files_card()
    {
        var (item, imagePath, _) = AddImageWithBlobs("remove-converted");
        _store.ConvertImageToFileCard(item.Id, imagePath);

        _store.Remove(item.Id);

        Assert.False(File.Exists(imagePath));
    }

    [Fact]
    public void Remove_never_deletes_an_ordinary_Files_path_outside_blobs_dir()
    {
        var outside = Path.Combine(_dir, "not-a-blob.txt");
        File.WriteAllText(outside, "keep me");
        var stack = ItemStore.BuildFileItems(new[] { outside }, DateTime.UtcNow)[0];
        _store.TryAdd(stack);

        _store.Remove(stack.Id);

        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void Remove_refuses_a_path_whose_prefix_merely_resembles_blobs_dir()
    {
        // Regression guard for a naive string-prefix containment check: a sibling directory whose
        // name starts with "blobs" (e.g. "blobsEvil") must NOT be treated as "under blobs\".
        var siblingDir = _store.BlobsDir + "Evil";
        Directory.CreateDirectory(siblingDir);
        var lookalike = Path.Combine(siblingDir, "x.png");
        File.WriteAllBytes(lookalike, new byte[] { 1 });

        var stack = ItemStore.BuildFileItems(new[] { lookalike }, DateTime.UtcNow)[0];
        _store.TryAdd(stack);

        _store.Remove(stack.Id);

        Assert.True(File.Exists(lookalike));
    }

    [Fact]
    public void Remove_deletes_a_blob_path_regardless_of_case_or_dot_segments()
    {
        var (item, imagePath, _) = AddImageWithBlobs("case-insensitive");
        var fileName = Path.GetFileName(imagePath);

        // Same file, spelled with an upper-cased drive letter and a redundant "." segment -- both
        // must normalize (Path.GetFullPath) to the same file the mixed-case/relative robustness
        // requirement calls for.
        var mixedCasePath = Path.Combine(
            _store.BlobsDir.ToUpperInvariant(), ".", fileName);

        _store.ConvertImageToFileCard(item.Id, mixedCasePath);
        _store.Remove(item.Id);

        Assert.False(File.Exists(imagePath));
    }

    [Fact]
    public void RemoveMany_deletes_both_blob_paths_of_a_merged_image_stack()
    {
        var (a, aPath, _) = AddImageWithBlobs("removemany-a");
        var (b, bPath, _) = AddImageWithBlobs("removemany-b");
        _store.ConvertImageToFileCard(a.Id, aPath);
        _store.ConvertImageToFileCard(b.Id, bPath);
        _store.TryMergeFiles(a.Id, b.Id);

        var mergedId = _store.Items.Single().Id;
        _store.RemoveMany(new[] { mergedId });

        Assert.False(File.Exists(aPath));
        Assert.False(File.Exists(bPath));
    }

    [Fact]
    public void SplitFile_moves_blob_ownership_so_deleting_the_old_stack_spares_the_split_out_blob()
    {
        var (a, aPath, _) = AddImageWithBlobs("split-a");
        var (b, bPath, _) = AddImageWithBlobs("split-b");
        _store.ConvertImageToFileCard(a.Id, aPath);
        _store.ConvertImageToFileCard(b.Id, bPath);
        _store.TryMergeFiles(a.Id, b.Id);
        var stackId = _store.Items.Single().Id;

        var splitCard = _store.SplitFile(stackId, bPath);
        Assert.NotNull(splitCard);

        // The remaining stack (now single-path aPath) no longer references bPath: deleting it must
        // not touch bPath's blob file -- ownership moved to splitCard, not duplicated.
        _store.Remove(stackId);
        Assert.True(File.Exists(bPath));
        Assert.False(File.Exists(aPath));
    }

    [Fact]
    public void SplitFile_split_out_card_deletion_removes_its_blob()
    {
        var (a, aPath, _) = AddImageWithBlobs("split-out-a");
        var (b, bPath, _) = AddImageWithBlobs("split-out-b");
        _store.ConvertImageToFileCard(a.Id, aPath);
        _store.ConvertImageToFileCard(b.Id, bPath);
        _store.TryMergeFiles(a.Id, b.Id);
        var stackId = _store.Items.Single().Id;

        var splitCard = _store.SplitFile(stackId, bPath)!;
        _store.Remove(splitCard.Id);

        Assert.False(File.Exists(bPath));
        Assert.True(File.Exists(aPath));
    }

    [Fact]
    public void Save_and_Load_round_trips_a_converted_card_with_no_image_specific_fields()
    {
        var (item, imagePath, _) = AddImageWithBlobs("persist");
        _store.ConvertImageToFileCard(item.Id, imagePath);
        _store.Save();

        var reloaded = new ItemStore(_dir);
        reloaded.Load();

        var loaded = reloaded.Items.Single(i => i.Id == item.Id);
        Assert.Equal(ClipKind.Files, loaded.Kind);
        Assert.Equal(new[] { imagePath }, loaded.Paths);
        Assert.Null(loaded.ImageFile);
        Assert.Null(loaded.ThumbFile);
    }
}
