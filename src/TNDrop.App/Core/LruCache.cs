using System;
using System.Collections.Generic;

namespace TNDrop.Core;

/// <summary>
/// A bounded, string-keyed least-recently-used cache.
///
/// <para>Storing a null value is meaningful and distinct from a miss, so a caller can memoise
/// "this key produced nothing" -- see ShellImaging, which caches failed shell extractions so a
/// card that re-renders does not re-ask the shell for an image it already refused.</para>
///
/// <para><b>Why the recency list holds nodes, not keys.</b> The obvious implementation pairs a
/// <c>Dictionary&lt;string, TValue&gt;</c> with a <c>LinkedList&lt;string&gt;</c> and calls
/// <c>_order.Remove(key)</c> to re-position an entry. That is quietly broken whenever the
/// dictionary's comparer is case-insensitive: <c>LinkedList.Remove</c> uses the default *ordinal*
/// string comparer, so a hit under different casing ("C:\X\A.TXT" for a key stored as
/// "C:\x\a.txt") finds nothing to remove and appends a second, orphaned node. The two structures
/// then disagree about how many entries exist, and the next eviction walks the orphans -- dropping
/// live entries, including the one just touched. Holding <see cref="LinkedListNode{T}"/> in the
/// dictionary removes the second comparer entirely: every move is by node identity, so the list
/// and the map cannot desync no matter what comparer the keys use.</para>
///
/// <para>Thread-safe: every operation takes the instance's own lock. The lock is held only for the
/// bookkeeping, so callers should compute values outside it.</para>
/// </summary>
public sealed class LruCache<TValue>
{
    /// <summary>The key is carried alongside the value so eviction can remove the map entry using
    /// the exact string the entry was inserted under.</summary>
    private readonly struct Entry(string key, TValue value)
    {
        public string Key { get; } = key;
        public TValue Value { get; } = value;
    }

    private readonly Dictionary<string, LinkedListNode<Entry>> _map;
    private readonly LinkedList<Entry> _order = new();
    private readonly object _lock = new();
    private readonly int _capacity;

    /// <param name="capacity">Maximum live entries; must be positive.</param>
    /// <param name="keyComparer">
    /// Defaults to <see cref="StringComparer.OrdinalIgnoreCase"/>, which is what both call sites
    /// want: keys are Windows paths, file names and extensions, and Windows treats those
    /// case-insensitively. Pass <see cref="StringComparer.Ordinal"/> for keys that are genuinely
    /// case-sensitive.
    /// </param>
    public LruCache(int capacity, StringComparer? keyComparer = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "capacity must be positive.");
        }

        _capacity = capacity;
        _map = new Dictionary<string, LinkedListNode<Entry>>(keyComparer ?? StringComparer.OrdinalIgnoreCase);
    }

    public int Capacity => _capacity;

    /// <summary>Live entries. Always equals the recency list's length.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _map.Count;
            }
        }
    }

    /// <summary>
    /// Looks <paramref name="key"/> up and, on a hit, marks it most recently used. Returns true
    /// for a stored null; <paramref name="value"/> is <c>default</c> on a miss.
    /// </summary>
    public bool TryGet(string key, out TValue value)
    {
        lock (_lock)
        {
            if (!_map.TryGetValue(key, out var node))
            {
                value = default!;
                return false;
            }

            // By node identity, so the casing of `key` is irrelevant.
            _order.Remove(node);
            _order.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
    }

    /// <summary>
    /// Inserts or replaces <paramref name="key"/>, marks it most recently used, and evicts the
    /// least recently used entries until the cache is back within <see cref="Capacity"/>.
    /// </summary>
    public void Set(string key, TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                // Replace in place: reuse the node so the list length tracks the map exactly.
                _order.Remove(existing);
                _map.Remove(key);
            }

            var node = _order.AddFirst(new Entry(key, value));
            _map[key] = node;

            while (_map.Count > _capacity)
            {
                var oldest = _order.Last!;
                _order.RemoveLast();
                _map.Remove(oldest.Value.Key);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _order.Clear();
        }
    }
}
