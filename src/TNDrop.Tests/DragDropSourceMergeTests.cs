using System;
using System.IO;
using System.Linq;
using TNDrop.Core;
using TNDrop.Platform;

/// <summary>
/// v1.3 Task B review fix: DragDropSource.TryPrepareCardsForMerge is the extracted, testable
/// decision behind ShelfWindow.OnCardDrop's "convert any Image side before merging" step. The
/// point of extracting it (rather than leaving the logic inline in OnCardDrop, which needs a real
/// WPF Window + OLE DragEventArgs to exercise) is specifically so the critical regression --
/// converting a healthy card before confirming the OTHER side is usable -- has direct coverage.
/// </summary>
public class DragDropSourceMergeTests : IDisposable
{
    // A real, minimal (1x1) PNG so BitmapImage decode -- which FullImagePath requires -- succeeds.
    private static readonly byte[] ValidPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tndrop-test-" + Guid.NewGuid());
    private readonly ItemStore _store;

    public DragDropSourceMergeTests()
    {
        _store = new ItemStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private ClipItem AddHealthyImage(string tag)
    {
        Directory.CreateDirectory(_store.BlobsDir);
        var imageFile = $"{tag}.png";
        File.WriteAllBytes(Path.Combine(_store.BlobsDir, imageFile), ValidPng);

        var item = new ClipItem
        {
            Kind = ClipKind.Image,
            ImageFile = imageFile,
            CreatedAtUtc = DateTime.UtcNow,
            ContentHash = ItemStore.Fnv1a(System.Text.Encoding.UTF8.GetBytes(tag)),
        };
        _store.TryAdd(item);
        return item;
    }

    private ClipItem AddUnresolvableImage(string tag)
    {
        // ImageFile/ThumbFile both name files that were never written -- FullImagePath resolves
        // to null for this item, the same as a blob deleted externally or quarantined by AV.
        var item = new ClipItem
        {
            Kind = ClipKind.Image,
            ImageFile = $"{tag}-missing.png",
            ThumbFile = $"{tag}-missing-thumb.png",
            CreatedAtUtc = DateTime.UtcNow,
            ContentHash = ItemStore.Fnv1a(System.Text.Encoding.UTF8.GetBytes(tag)),
        };
        _store.TryAdd(item);
        return item;
    }

    [StaFact]
    public void TryPrepareCardsForMerge_converts_two_healthy_images()
    {
        var target = AddHealthyImage("target");
        var source = AddHealthyImage("source");

        var ok = DragDropSource.TryPrepareCardsForMerge(_store, _store.BlobsDir, target, source);

        Assert.True(ok);
        Assert.Equal(ClipKind.Files, _store.Items.Single(i => i.Id == target.Id).Kind);
        Assert.Equal(ClipKind.Files, _store.Items.Single(i => i.Id == source.Id).Kind);
    }

    [StaFact]
    public void TryPrepareCardsForMerge_passes_through_two_Files_cards_untouched()
    {
        var target = ItemStore.BuildFileItems(new[] { @"C:\a.txt" }, DateTime.UtcNow)[0];
        var source = ItemStore.BuildFileItems(new[] { @"C:\b.txt" }, DateTime.UtcNow)[0];
        _store.TryAdd(target);
        _store.TryAdd(source);

        var ok = DragDropSource.TryPrepareCardsForMerge(_store, _store.BlobsDir, target, source);

        Assert.True(ok);
        Assert.Equal(new[] { @"C:\a.txt" }, _store.Items.Single(i => i.Id == target.Id).Paths);
        Assert.Equal(new[] { @"C:\b.txt" }, _store.Items.Single(i => i.Id == source.Id).Paths);
    }

    // ---- the critical fix: no observable mutation on ANY refusal path --------------------------

    [StaFact]
    public void TryPrepareCardsForMerge_refuses_and_leaves_a_healthy_target_untouched_when_source_is_unresolvable()
    {
        // Regression for the exact bug flagged in review round 1: the target used to be converted
        // (Kind flipped Image -> Files, file renamed) as a side effect of evaluating the target
        // half of the check, BEFORE the source half -- which could still refuse the whole drop --
        // ever ran. A healthy card sitting next to a broken one must come out of a refused drop
        // completely unchanged.
        var target = AddHealthyImage("healthy-target");
        var source = AddUnresolvableImage("broken-source");
        var targetImageFileBefore = target.ImageFile;

        var ok = DragDropSource.TryPrepareCardsForMerge(_store, _store.BlobsDir, target, source);

        Assert.False(ok);

        var storedTarget = _store.Items.Single(i => i.Id == target.Id);
        Assert.Equal(ClipKind.Image, storedTarget.Kind);
        Assert.Equal(targetImageFileBefore, storedTarget.ImageFile);
        Assert.Empty(storedTarget.Paths);

        // The original blob file must not have been touched (renamed away) either.
        Assert.True(File.Exists(Path.Combine(_store.BlobsDir, targetImageFileBefore!)));

        var storedSource = _store.Items.Single(i => i.Id == source.Id);
        Assert.Equal(ClipKind.Image, storedSource.Kind);
    }

    [StaFact]
    public void TryPrepareCardsForMerge_refuses_and_leaves_a_healthy_source_untouched_when_target_is_unresolvable()
    {
        // Symmetric case: confirms the fix defers mutation for BOTH sides, not just the one that
        // happens to be checked first.
        var target = AddUnresolvableImage("broken-target");
        var source = AddHealthyImage("healthy-source");
        var sourceImageFileBefore = source.ImageFile;

        var ok = DragDropSource.TryPrepareCardsForMerge(_store, _store.BlobsDir, target, source);

        Assert.False(ok);

        var storedSource = _store.Items.Single(i => i.Id == source.Id);
        Assert.Equal(ClipKind.Image, storedSource.Kind);
        Assert.Equal(sourceImageFileBefore, storedSource.ImageFile);
        Assert.True(File.Exists(Path.Combine(_store.BlobsDir, sourceImageFileBefore!)));

        var storedTarget = _store.Items.Single(i => i.Id == target.Id);
        Assert.Equal(ClipKind.Image, storedTarget.Kind);
    }

    [StaFact]
    public void TryPrepareCardsForMerge_refuses_when_both_sides_are_unresolvable()
    {
        var target = AddUnresolvableImage("broken-target-2");
        var source = AddUnresolvableImage("broken-source-2");

        var ok = DragDropSource.TryPrepareCardsForMerge(_store, _store.BlobsDir, target, source);

        Assert.False(ok);
        Assert.Equal(ClipKind.Image, _store.Items.Single(i => i.Id == target.Id).Kind);
        Assert.Equal(ClipKind.Image, _store.Items.Single(i => i.Id == source.Id).Kind);
    }

    // ---- v1.3 review fix I3: the prep step's conversion must survive a SUBSEQUENT cap refusal ---

    [StaFact]
    public void Prep_then_cap_refused_merge_still_persists_the_prep_conversion_when_saved()
    {
        // Reproduces ShelfWindow.OnCardDrop's exact sequence: TryPrepareCardsForMerge runs and
        // succeeds (converting the healthy Image source to Files, renaming its blob on disk)
        // BEFORE TryMergeFiles is even called -- the 10-file cap is a property of the SECOND step,
        // which has no way to undo the first. Before the I3 fix, OnCardDrop's cap-refusal branch
        // returned without ever calling _itemStore.Save(), so a fresh load (simulating the next
        // launch, or a crash before any later unrelated save) would still see this card as
        // Kind=Image pointing at a ThumbFile that TryPrepareCardsForMerge already deleted from
        // disk -- a broken card. This test drives ItemStore/DragDropSource directly (no WPF Window
        // or OLE DragEventArgs needed, unlike OnCardDrop itself) through prepare -> refuse -> save
        // -> reload, and asserts the reload sees the converted Files card, not the stale Image one.
        var target = ItemStore.BuildFileItems(
            Enumerable.Range(1, 10).Select(i => $@"C:\cap\{i}.txt").ToArray(), DateTime.UtcNow)[0];
        _store.TryAdd(target);
        var source = AddHealthyImage("cap-source");

        var prepped = DragDropSource.TryPrepareCardsForMerge(_store, _store.BlobsDir, target, source);
        Assert.True(prepped);
        Assert.Equal(ClipKind.Files, _store.Items.Single(i => i.Id == source.Id).Kind);

        var merged = _store.TryMergeFiles(target.Id, source.Id);
        Assert.False(merged); // 10 + 1 > 10 -- refused, exactly as OnCardDrop's cap branch expects

        // The fix: OnCardDrop's refusal branch now saves here, same as this line.
        _store.Save();

        var reloaded = new ItemStore(_dir);
        reloaded.Load();

        var reloadedSource = reloaded.Items.Single(i => i.Id == source.Id);
        Assert.Equal(ClipKind.Files, reloadedSource.Kind);
        Assert.Single(reloadedSource.Paths);

        // Both cards remain distinct (the refused merge did not combine them) and the target is
        // untouched by the refusal.
        var reloadedTarget = reloaded.Items.Single(i => i.Id == target.Id);
        Assert.Equal(10, reloadedTarget.Paths.Count);
    }
}
