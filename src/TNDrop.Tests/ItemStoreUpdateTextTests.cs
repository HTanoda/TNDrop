using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TNDrop.Core;

public class ItemStoreUpdateTextTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tndrop-test-" + Guid.NewGuid());

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static ClipItem NewText(string text) => new()
    {
        Kind = ClipKind.Text,
        Text = text,
        CreatedAtUtc = DateTime.UtcNow,
        ContentHash = ItemStore.Fnv1a(Encoding.UTF8.GetBytes(text)),
    };

    [Fact]
    public void UpdateText_TextStaysText_UpdatesTextAndHash()
    {
        var store = new ItemStore(_dir);
        var item = NewText("before");
        store.TryAdd(item);

        var ok = store.UpdateText(item.Id, "after");

        Assert.True(ok);
        var updated = store.Items.Single(i => i.Id == item.Id);
        Assert.Equal("after", updated.Text);
        Assert.Equal(ClipKind.Text, updated.Kind);
        Assert.Equal(ItemStore.Fnv1a(Encoding.UTF8.GetBytes("after")), updated.ContentHash);
    }

    [Fact]
    public void UpdateText_TextBecomesUrl_ReclassifiesToLink()
    {
        // 先頭の h が欠けた URL に h を足すユースケース (設計書 §2 の例そのもの)
        var store = new ItemStore(_dir);
        var item = NewText("ttps://example.com/page");
        store.TryAdd(item);

        var ok = store.UpdateText(item.Id, "https://example.com/page");

        Assert.True(ok);
        var updated = store.Items.Single(i => i.Id == item.Id);
        Assert.Equal(ClipKind.Link, updated.Kind);
        Assert.Equal("https://example.com/page", updated.Text);
        Assert.Equal(ItemStore.Fnv1a(Encoding.UTF8.GetBytes("https://example.com/page")), updated.ContentHash);
    }

    [Fact]
    public void UpdateText_LinkBecomesText_ReclassifiesToText()
    {
        var store = new ItemStore(_dir);
        var item = new ClipItem
        {
            Kind = ClipKind.Link,
            Text = "https://example.com/",
            CreatedAtUtc = DateTime.UtcNow,
            ContentHash = ItemStore.Fnv1a(Encoding.UTF8.GetBytes("https://example.com/")),
        };
        store.TryAdd(item);

        var ok = store.UpdateText(item.Id, "ただのメモ");

        Assert.True(ok);
        Assert.Equal(ClipKind.Text, store.Items.Single(i => i.Id == item.Id).Kind);
    }

    [Fact]
    public void UpdateText_PreservesIdCreatedAtAndPinned()
    {
        var store = new ItemStore(_dir);
        var created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var item = NewText("before");
        item.CreatedAtUtc = created;
        store.TryAdd(item);
        store.SetPinned(item.Id, true);

        store.UpdateText(item.Id, "after");

        var updated = store.Items.Single(i => i.Id == item.Id);
        Assert.Equal(item.Id, updated.Id);
        Assert.Equal(created, updated.CreatedAtUtc);
        Assert.True(updated.Pinned);
    }

    [Fact]
    public void UpdateText_UnknownId_ReturnsFalse_NoChange()
    {
        var store = new ItemStore(_dir);
        var item = NewText("keep");
        store.TryAdd(item);

        Assert.False(store.UpdateText("no-such-id", "x"));
        Assert.Equal("keep", store.Items.Single().Text);
    }

    [Fact]
    public void UpdateText_NonTextKinds_ReturnsFalse_NoChange()
    {
        var store = new ItemStore(_dir);
        var files = new ClipItem
        {
            Kind = ClipKind.Files,
            Paths = new List<string> { @"C:\a.txt" },
            CreatedAtUtc = DateTime.UtcNow,
            ContentHash = ItemStore.Fnv1a(Encoding.UTF8.GetBytes(@"C:\a.txt")),
        };
        store.TryAdd(files);

        Assert.False(store.UpdateText(files.Id, "x"));
        Assert.Equal(ClipKind.Files, store.Items.Single().Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void UpdateText_BlankText_Throws(string? blank)
    {
        var store = new ItemStore(_dir);
        var item = NewText("keep");
        store.TryAdd(item);

        Assert.Throws<ArgumentException>(() => store.UpdateText(item.Id, blank!));
        Assert.Equal("keep", store.Items.Single().Text);
    }

    [Fact]
    public void UpdateText_RaisesChanged()
    {
        var store = new ItemStore(_dir);
        var item = NewText("before");
        store.TryAdd(item);
        var changed = 0;
        store.Changed += () => changed++;

        store.UpdateText(item.Id, "after");

        Assert.Equal(1, changed);
    }

    [Fact]
    public void UpdateText_ThenSave_PersistsAcrossReload()
    {
        var store = new ItemStore(_dir);
        var item = NewText("before");
        store.TryAdd(item);
        store.UpdateText(item.Id, "https://example.com/edited");
        store.Save();

        var reloaded = new ItemStore(_dir);
        reloaded.Load();
        var loaded = reloaded.Items.Single(i => i.Id == item.Id);
        Assert.Equal("https://example.com/edited", loaded.Text);
        Assert.Equal(ClipKind.Link, loaded.Kind);
    }
}
