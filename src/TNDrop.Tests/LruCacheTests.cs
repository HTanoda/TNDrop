using TNDrop.Core;

namespace TNDrop.Tests;

/// <summary>
/// LruCache は「キーの辞書」と「LRU 順序リスト」の 2 構造を持つ。両者が食い違うと、
/// 直前に使ったばかりのエントリが追い出される (静かなキャッシュミス) ため、
/// 大文字小文字違いのキーを含めて両構造が常に一致することを固定する。
/// </summary>
public class LruCacheTests
{
    [Fact]
    public void TryGet_on_empty_cache_misses()
    {
        var cache = new LruCache<string>(4);
        Assert.False(cache.TryGet("nope", out _));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Set_then_TryGet_returns_the_value()
    {
        var cache = new LruCache<string>(4);
        cache.Set("a", "one");

        Assert.True(cache.TryGet("a", out var value));
        Assert.Equal("one", value);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Stored_null_is_a_hit_not_a_miss()
    {
        // ShellImaging は「シェルが画像を返さなかった」ことを null で記憶する。
        // これがミス扱いになると毎回 COM を呼び直してしまう。
        var cache = new LruCache<string?>(4);
        cache.Set("a", null);

        Assert.True(cache.TryGet("a", out var value));
        Assert.Null(value);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Set_overwrites_without_growing()
    {
        var cache = new LruCache<string>(4);
        cache.Set("a", "one");
        cache.Set("a", "two");

        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet("a", out var value));
        Assert.Equal("two", value);
    }

    [Fact]
    public void Least_recently_used_entry_is_evicted_first()
    {
        var cache = new LruCache<string>(2);
        cache.Set("a", "1");
        cache.Set("b", "2");

        Assert.True(cache.TryGet("a", out _));   // a を最近使用にする
        cache.Set("c", "3");                     // 追い出されるのは b

        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("c", out _));
        Assert.False(cache.TryGet("b", out _));
    }

    [Fact]
    public void TryGet_with_different_casing_promotes_the_same_entry()
    {
        // 回帰テスト: 順序リストを文字列で線形検索していた頃は、大文字小文字が違うと
        // 既存ノードを消せず孤児ノードが増え、_order.Count だけが容量を超えて
        // 「いま使ったばかりのエントリ」が追い出されていた。
        var cache = new LruCache<string>(2);
        cache.Set(@"C:\x\a.txt", "1");
        cache.Set(@"C:\x\b.txt", "2");

        // 別ケーシングでヒットさせる。ここで孤児ノードができると、この時点で
        // a.txt が追い出されてしまう。
        Assert.True(cache.TryGet(@"C:\X\A.TXT", out var value));
        Assert.Equal("1", value);
        Assert.Equal(2, cache.Count);

        cache.Set(@"C:\x\c.txt", "3");

        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGet(@"C:\x\a.txt", out _));   // 直前に使ったので残る
        Assert.True(cache.TryGet(@"C:\x\c.txt", out _));
        Assert.False(cache.TryGet(@"C:\x\b.txt", out _));  // 最も古いので消える
    }

    [Fact]
    public void Set_with_different_casing_updates_the_same_entry()
    {
        var cache = new LruCache<string>(4);
        cache.Set(@"C:\x\a.txt", "1");
        cache.Set(@"C:\X\A.TXT", "2");

        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet(@"C:\x\a.txt", out var value));
        Assert.Equal("2", value);
    }

    [Fact]
    public void Repeated_case_varying_hits_never_grow_the_cache()
    {
        var cache = new LruCache<string>(2);
        cache.Set("alpha", "1");
        cache.Set("beta", "2");

        for (var i = 0; i < 50; i++)
        {
            Assert.True(cache.TryGet(i % 2 == 0 ? "ALPHA" : "Alpha", out _));
            Assert.Equal(2, cache.Count);
        }

        // 50 回触った alpha が生き残り、一度も触っていない beta が残っていること。
        Assert.True(cache.TryGet("alpha", out _));
        Assert.True(cache.TryGet("beta", out _));
    }

    [Fact]
    public void Capacity_of_one_keeps_only_the_newest()
    {
        var cache = new LruCache<string>(1);
        cache.Set("a", "1");
        cache.Set("b", "2");

        Assert.Equal(1, cache.Count);
        Assert.False(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("b", out _));
    }

    [Fact]
    public void Never_exceeds_capacity_under_many_distinct_keys()
    {
        var cache = new LruCache<int>(8);

        for (var i = 0; i < 200; i++)
        {
            cache.Set($"key-{i}", i);
            Assert.True(cache.Count <= 8);
        }

        Assert.Equal(8, cache.Count);
        Assert.True(cache.TryGet("key-199", out var newest));
        Assert.Equal(199, newest);
        Assert.False(cache.TryGet("key-0", out _));
    }

    [Fact]
    public void Clear_empties_the_cache()
    {
        var cache = new LruCache<string>(4);
        cache.Set("a", "1");
        cache.Set("b", "2");
        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet("a", out _));

        // Clear 後も通常どおり使えること (順序リストも空になっている)。
        cache.Set("c", "3");
        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet("c", out _));
    }

    [Fact]
    public void Ordinal_comparer_keeps_case_varying_keys_separate()
    {
        var cache = new LruCache<string>(4, StringComparer.Ordinal);
        cache.Set("a", "lower");
        cache.Set("A", "upper");

        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGet("a", out var lower));
        Assert.True(cache.TryGet("A", out var upper));
        Assert.Equal("lower", lower);
        Assert.Equal("upper", upper);
    }

    [Fact]
    public void Non_positive_capacity_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LruCache<string>(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LruCache<string>(-1));
    }
}
