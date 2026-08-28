using System.IO;
using System.Linq;
using System.Text.Json;
using TNDrop.Core;

public class ItemStoreBackupApiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tndrop-test-" + Guid.NewGuid());
    private readonly string _dest = Path.Combine(Path.GetTempPath(), "tndrop-test-" + Guid.NewGuid());

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
        try { Directory.Delete(_dest, true); } catch { }
    }

    private ItemStore NewStoreWithOneItem(string text)
    {
        var store = new ItemStore(_dir);
        store.TryAdd(new ClipItem { Kind = ClipKind.Text, Text = text, CreatedAtUtc = DateTime.UtcNow });
        store.Save();
        return store;
    }

    [Fact]
    public void Saved_RaisedAfterSuccessfulSave()
    {
        var store = new ItemStore(_dir);
        var raised = 0;
        store.Saved += () => raised++;
        store.Save();
        Assert.Equal(1, raised);
    }

    [Fact]
    public void CopyDataTo_CopiesItemsDatAndBlobs()
    {
        var store = NewStoreWithOneItem("hello");
        File.WriteAllBytes(Path.Combine(store.BlobsDir, "img1.png"), new byte[] { 1, 2, 3 });

        store.CopyDataTo(_dest);

        Assert.True(File.Exists(Path.Combine(_dest, "items.dat")));
        Assert.True(File.Exists(Path.Combine(_dest, "blobs", "img1.png")));
    }

    // items.dat が無く items.bak だけが残る状態 (Save() の File.Replace 中のクラッシュ、
    // items.dat の外部削除) では、Load() は items.bak から復旧する。CopyDataTo も同じ
    // フォールバックをしなければ、その状態で取ったバックアップが「履歴 0 件」になり、
    // それで巻き戻すと救えたはずの履歴を消す (v1.6 Task 5 レビュー修正)。
    [Fact]
    public void CopyDataTo_FallsBackToItemsBak_WhenItemsDatIsMissing()
    {
        var store = NewStoreWithOneItem("recoverable");
        File.Move(Path.Combine(_dir, "items.dat"), Path.Combine(_dir, "items.bak"));

        store.CopyDataTo(_dest);

        var copied = Path.Combine(_dest, "items.dat");
        Assert.True(File.Exists(copied));
        Assert.True(ItemStore.CanDecrypt(copied));

        var reloaded = new ItemStore(_dest);
        reloaded.Load();
        Assert.Contains(reloaded.Items, i => i.Text == "recoverable");
    }

    [Fact]
    public void ReadDecryptedJson_ReturnsJsonContainingItemText()
    {
        // System.Text.Json's default encoder escapes non-ASCII characters (e.g. \uXXXX for
        // Japanese) when it serializes, so the raw text does not appear as a literal substring
        // of the JSON -- parse it back out instead of doing a Contains() on the raw string.
        var store = NewStoreWithOneItem("秘密のテキスト");
        var json = store.ReadDecryptedJson();
        Assert.NotNull(json);

        using var doc = JsonDocument.Parse(json!);
        Assert.Equal("秘密のテキスト", doc.RootElement[0].GetProperty("Text").GetString());
    }

    // items.dat が無く items.bak だけが残る状態は Load() が復旧できる「読める履歴あり」の状態
    // (CopyDataTo も同じフォールバックを持つ)。ReadDecryptedJson だけが null を返すと、
    // ExportTo がその null を "[]" に潰して**中身が空のエクスポート**を書き、それを取り込んだ
    // 移行先の履歴が消える。「Load() が読むファイルはどれか」の答えを 3 箇所で揃える
    // (v1.6 最終レビュー修正 Fix 3)。
    [Fact]
    public void ReadDecryptedJson_FallsBackToItemsBak_WhenItemsDatIsMissing()
    {
        var store = NewStoreWithOneItem("recoverable-export");
        File.Move(Path.Combine(_dir, "items.dat"), Path.Combine(_dir, "items.bak"));

        var json = store.ReadDecryptedJson();

        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.Equal("recoverable-export", doc.RootElement[0].GetProperty("Text").GetString());
    }

    [Fact]
    public void ReadDecryptedJson_NoFile_ReturnsNull()
    {
        var store = new ItemStore(_dir);
        Assert.Null(store.ReadDecryptedJson());
    }

    [Fact]
    public void WriteEncryptedJson_ProducesFileThatLoadLoads()
    {
        var store = NewStoreWithOneItem("roundtrip");
        var json = store.ReadDecryptedJson()!;

        var otherDir = Path.Combine(_dest, "other");
        Directory.CreateDirectory(otherDir);
        ItemStore.WriteEncryptedJson(Path.Combine(otherDir, "items.dat"), json);

        var other = new ItemStore(otherDir);
        other.Load();
        Assert.Contains(other.Items, i => i.Text == "roundtrip");
    }

    [Fact]
    public void CanDecrypt_TrueForOwnFile_FalseForGarbage()
    {
        var store = NewStoreWithOneItem("x");
        Assert.True(ItemStore.CanDecrypt(Path.Combine(_dir, "items.dat")));

        var garbage = Path.Combine(_dest, "items.dat");
        Directory.CreateDirectory(_dest);
        File.WriteAllBytes(garbage, new byte[] { 9, 9, 9, 9 });
        Assert.False(ItemStore.CanDecrypt(garbage));
    }

    [Fact]
    public void ReplaceDataFrom_CorruptItemsDat_Throws()
    {
        var source = NewStoreWithOneItem("keep");
        Directory.CreateDirectory(_dest);
        File.WriteAllBytes(Path.Combine(_dest, "items.dat"), new byte[] { 1, 2, 3, 4 });

        Assert.Throws<InvalidDataException>(() => source.ReplaceDataFrom(_dest));

        // Contract: InvalidDataException means nothing was touched -- neither the live store's
        // in-memory items nor its own items.dat on disk changed.
        Assert.Contains(source.Items, i => i.Text == "keep");

        var reloaded = new ItemStore(_dir);
        reloaded.Load();
        Assert.False(reloaded.LoadFailed);
        Assert.Contains(reloaded.Items, i => i.Text == "keep");
    }

    [Fact]
    public void ReplaceDataFrom_SwapsContentAndRaisesChanged()
    {
        var source = NewStoreWithOneItem("old-content");
        var stagingStore = new ItemStore(_dest);
        stagingStore.TryAdd(new ClipItem { Kind = ClipKind.Text, Text = "new-content", CreatedAtUtc = DateTime.UtcNow });
        stagingStore.Save();

        var changed = false;
        source.Changed += () => changed = true;
        source.ReplaceDataFrom(_dest);

        Assert.True(changed);
        Assert.Contains(source.Items, i => i.Text == "new-content");
        Assert.DoesNotContain(source.Items, i => i.Text == "old-content");
    }

    [Fact]
    public void ReplaceDataFrom_ReplacesBlobsWithSourceDirBlobsOnly()
    {
        var source = NewStoreWithOneItem("old-content");
        File.WriteAllBytes(Path.Combine(source.BlobsDir, "old.png"), new byte[] { 1 });

        var stagingStore = new ItemStore(_dest);
        stagingStore.TryAdd(new ClipItem { Kind = ClipKind.Text, Text = "new-content", CreatedAtUtc = DateTime.UtcNow });
        stagingStore.Save();
        File.WriteAllBytes(Path.Combine(stagingStore.BlobsDir, "new1.png"), new byte[] { 2, 2 });
        File.WriteAllBytes(Path.Combine(stagingStore.BlobsDir, "new2.png"), new byte[] { 3, 3, 3 });

        var changed = false;
        source.Changed += () => changed = true;
        source.ReplaceDataFrom(_dest);

        Assert.True(changed);
        Assert.Contains(source.Items, i => i.Text == "new-content");
        Assert.DoesNotContain(source.Items, i => i.Text == "old-content");

        var remainingBlobNames = Directory.GetFiles(source.BlobsDir)
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(new[] { "new1.png", "new2.png" }, remainingBlobNames);
    }
}
