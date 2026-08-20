using System;
using System.IO;
using TNDrop.Core;

public class ItemStorePersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tndrop-test-" + Guid.NewGuid());
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static ClipItem TextItem(string text) => new()
    {
        Kind = ClipKind.Text, Text = text, CreatedAtUtc = DateTime.UtcNow,
        ContentHash = ItemStore.Fnv1a(System.Text.Encoding.UTF8.GetBytes(text))
    };

    [Fact]
    public void Save_then_load_roundtrips_items()
    {
        var store = new ItemStore(_dir);
        store.TryAdd(TextItem("hello"));
        store.Save();
        var store2 = new ItemStore(_dir);
        store2.Load();
        Assert.Single(store2.Items);
        Assert.Equal("hello", store2.Items[0].Text);
        Assert.False(store2.LoadFailed);
    }

    [Fact]
    public void Saved_file_is_not_plaintext()
    {
        var store = new ItemStore(_dir);
        store.TryAdd(TextItem("SECRETWORD"));
        store.Save();
        var raw = File.ReadAllBytes(Path.Combine(_dir, "items.dat"));
        Assert.DoesNotContain("SECRETWORD", System.Text.Encoding.UTF8.GetString(raw));
    }

    [Fact]
    public void Load_recovers_from_bak_when_dat_corrupt()
    {
        var store = new ItemStore(_dir);
        store.TryAdd(TextItem("first"));
        store.Save();                                  // items.dat 作成
        store.TryAdd(TextItem("second"));
        store.Save();                                  // dat=2件, bak=1件
        File.WriteAllBytes(Path.Combine(_dir, "items.dat"), new byte[] { 1, 2, 3 });
        var store2 = new ItemStore(_dir);
        store2.Load();
        Assert.Single(store2.Items);                   // bak から復旧
        Assert.False(store2.LoadFailed);
    }

    [Fact]
    public void Load_yields_empty_and_flags_when_both_corrupt()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "items.dat"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_dir, "items.bak"), new byte[] { 2 });
        var store = new ItemStore(_dir);
        store.Load();
        Assert.Empty(store.Items);
        Assert.True(store.LoadFailed);
    }
}
