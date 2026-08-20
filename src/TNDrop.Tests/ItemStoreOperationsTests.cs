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
}
