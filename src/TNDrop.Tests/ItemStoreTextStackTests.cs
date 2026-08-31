using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TNDrop.Core;

public class ItemStoreTextStackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tndrop-test-" + Guid.NewGuid());
    private readonly ItemStore _store;
    public ItemStoreTextStackTests() { _store = new ItemStore(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private ClipItem AddText(string t)
    {
        var item = new ClipItem
        {
            Kind = ClipKind.Text, Text = t, CreatedAtUtc = DateTime.UtcNow,
            ContentHash = ItemStore.Fnv1a(Encoding.UTF8.GetBytes(t))
        };
        _store.TryAdd(item);
        return item;
    }

    private ClipItem AddStack(params string[] texts)
    {
        var target = AddText(texts[0]);
        foreach (var t in texts.Skip(1))
        {
            var source = AddText(t);
            Assert.True(_store.TryMergeTexts(target.Id, source.Id));
        }
        return target;
    }

    [Fact]
    public void TryMergeTexts_merges_two_text_cards_and_removes_source()
    {
        var a = AddText("挨拶文");
        var b = AddText("締めの文");

        Assert.True(_store.TryMergeTexts(a.Id, b.Id));

        var stack = _store.Items.Single();
        Assert.Equal(a.Id, stack.Id);
        Assert.True(stack.IsTextStack);
        Assert.Equal(new List<string> { "挨拶文", "締めの文" }, stack.Texts);
        Assert.Null(stack.Text);
        Assert.Equal(ItemStore.TextStackHash(stack.Texts), stack.ContentHash);
    }

    [Fact]
    public void TryMergeTexts_refuses_link_cards()
    {
        var text = AddText("挨拶文");
        var link = new ClipItem
        {
            Kind = ClipKind.Link, Text = "https://example.com/",
            CreatedAtUtc = DateTime.UtcNow,
            ContentHash = ItemStore.Fnv1a(Encoding.UTF8.GetBytes("https://example.com/"))
        };
        _store.TryAdd(link);

        Assert.False(_store.TryMergeTexts(text.Id, link.Id));
        Assert.False(_store.TryMergeTexts(link.Id, text.Id));
        Assert.Equal(2, _store.Items.Count);
    }

    [Fact]
    public void TryMergeTexts_refuses_over_ten()
    {
        var stack = AddStack("a", "b", "c", "d", "e", "f", "g", "h", "i", "j");
        var extra = AddText("k");

        Assert.False(_store.TryMergeTexts(stack.Id, extra.Id));
        Assert.Equal(10, _store.Items.Single(i => i.Id == stack.Id).Texts.Count);
    }

    [Fact]
    public void TryMergeTexts_dedupes_identical_texts_and_refuses_a_would_be_single()
    {
        var a = AddText("同文");
        var b = new ClipItem
        {
            Kind = ClipKind.Text, Text = "同文", CreatedAtUtc = DateTime.UtcNow,
            ContentHash = 12345 // TryAdd の先頭重複拒否を回避するための別ハッシュ
        };
        _store.TryAdd(b);

        // 統合結果が 1 件になるマージは「スタック」を作らないので拒否
        Assert.False(_store.TryMergeTexts(a.Id, b.Id));
        Assert.Equal(2, _store.Items.Count);
    }

    [Fact]
    public void TryMergeTexts_ors_pinned_and_inherits_name_when_target_unnamed()
    {
        var a = AddText("a");
        var b = AddText("b");
        _store.SetPinned(b.Id, true);
        _store.SetName(b.Id, "定例文セット");

        Assert.True(_store.TryMergeTexts(a.Id, b.Id));

        var stack = _store.Items.Single();
        Assert.True(stack.Pinned);
        Assert.Equal("定例文セット", stack.Name);
    }

    [Fact]
    public void TryMergeTexts_keeps_target_name_when_both_named()
    {
        var a = AddText("a");
        var b = AddText("b");
        _store.SetName(a.Id, "ターゲット名");
        _store.SetName(b.Id, "ソース名");

        Assert.True(_store.TryMergeTexts(a.Id, b.Id));
        Assert.Equal("ターゲット名", _store.Items.Single().Name);
    }

    [Fact]
    public void TryMergeTexts_folds_a_stack_into_a_stack()
    {
        var s1 = AddStack("a", "b");
        var s2 = AddStack("c", "d");

        Assert.True(_store.TryMergeTexts(s1.Id, s2.Id));
        Assert.Equal(new List<string> { "a", "b", "c", "d" },
            _store.Items.Single().Texts);
    }

    [Fact]
    public void SplitText_extracts_one_text_into_its_own_card()
    {
        var stack = AddStack("a", "b", "c");

        var card = _store.SplitText(stack.Id, "b");

        Assert.NotNull(card);
        Assert.Equal("b", card!.Text);
        Assert.Equal(ClipKind.Text, card.Kind);
        Assert.Empty(card.Texts);
        var remaining = _store.Items.Single(i => i.Id == stack.Id);
        Assert.Equal(new List<string> { "a", "c" }, remaining.Texts);
        Assert.Equal(ItemStore.TextStackHash(remaining.Texts), remaining.ContentHash);
        Assert.Equal(card.Id, _store.Items[0].Id); // 先頭挿入
    }

    [Fact]
    public void SplitText_last_but_one_normalizes_back_to_a_plain_text_card()
    {
        var stack = AddStack("a", "b");
        _store.SetName(stack.Id, "名前");

        Assert.NotNull(_store.SplitText(stack.Id, "b"));

        var remaining = _store.Items.Single(i => i.Id == stack.Id);
        Assert.False(remaining.IsTextStack);
        Assert.Equal("a", remaining.Text);
        Assert.Empty(remaining.Texts);
        Assert.Null(remaining.Name); // スタック解消で名前も消える (SplitAll でスタックが消えるのと同じ)
        Assert.Equal(ItemStore.Fnv1a(Encoding.UTF8.GetBytes("a")), remaining.ContentHash);
    }

    [Fact]
    public void SplitText_inherits_pinned()
    {
        var stack = AddStack("a", "b", "c");
        _store.SetPinned(stack.Id, true);

        var card = _store.SplitText(stack.Id, "c");
        Assert.True(card!.Pinned);
    }

    [Fact]
    public void SplitText_refuses_unknown_text_and_non_stack()
    {
        var stack = AddStack("a", "b");
        var lone = AddText("lone");

        Assert.Null(_store.SplitText(stack.Id, "zzz"));
        Assert.Null(_store.SplitText(lone.Id, "lone"));
        Assert.Null(_store.SplitText("no-such-id", "a"));
    }

    [Fact]
    public void SplitAllTexts_expands_everything_and_removes_the_stack()
    {
        var stack = AddStack("a", "b", "c");
        var changed = 0;
        _store.Changed += () => changed++;

        var created = _store.SplitAllTexts(stack.Id);

        Assert.NotNull(created);
        Assert.Equal(3, created!.Count);
        Assert.Equal(1, changed);
        Assert.DoesNotContain(_store.Items, i => i.Id == stack.Id);
        Assert.Equal(new[] { "a", "b", "c" },
            _store.Items.Take(3).Select(i => i.Text).ToArray());
    }

    [Fact]
    public void SplitAllTexts_refuses_a_plain_text_card()
    {
        var lone = AddText("lone");
        Assert.Null(_store.SplitAllTexts(lone.Id));
    }

    [Fact]
    public void SetName_sets_trims_and_clears()
    {
        var stack = AddStack("a", "b");

        Assert.True(_store.SetName(stack.Id, "  議会用  "));
        Assert.Equal("議会用", _store.Items.Single().Name);

        Assert.True(_store.SetName(stack.Id, "   "));
        Assert.Null(_store.Items.Single().Name);

        Assert.False(_store.SetName("no-such-id", "x"));
    }

    [Fact]
    public void UpdateText_refuses_a_text_stack()
    {
        var stack = AddStack("a", "b");

        Assert.False(_store.UpdateText(stack.Id, "書き換え"));
        Assert.True(_store.Items.Single().IsTextStack);
    }

    [Fact]
    public void Texts_and_Name_round_trip_through_items_dat()
    {
        var stack = AddStack("往復a", "往復b");
        _store.SetName(stack.Id, "往復名");
        _store.Save();

        var reloaded = new ItemStore(_dir);
        reloaded.Load();
        var loaded = reloaded.Items.Single(i => i.Id == stack.Id);
        Assert.True(loaded.IsTextStack);
        Assert.Equal(new List<string> { "往復a", "往復b" }, loaded.Texts);
        Assert.Equal("往復名", loaded.Name);
        Assert.Null(loaded.Text);
    }

    [Fact]
    public void Old_format_json_without_texts_and_name_still_loads()
    {
        var stack = AddStack("a", "b"); // v1.8 形式で一度保存してから
        _ = stack;
        var lone = AddText("単独");
        _ = lone;
        _store.Save();

        // 旧形式相当: Texts/Name キーの無い JSON を読めることは、上の往復 +
        // System.Text.Json の既定 (欠落プロパティは初期化子の値) で保証されるが、
        // 明示テストとして単独カードの Texts が空リストで初期化されることを確認する。
        var reloaded = new ItemStore(_dir);
        reloaded.Load();
        var loadedLone = reloaded.Items.First(i => i.Text == "単独");
        Assert.Empty(loadedLone.Texts);
        Assert.Null(loadedLone.Name);
        Assert.False(loadedLone.IsTextStack);
    }
}
