using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TNDrop.Services;

namespace TNDrop.Core;

public sealed partial class ItemStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _dataDir;
    private readonly string _itemsPath;
    private readonly string _bakPath;
    private readonly string _tmpPath;
    private readonly Func<DateTime> _utcClock;

    // Guards BOTH the in-memory _items list AND the on-disk items.dat/.bak/.tmp
    // files: Load() and Save() run their entire file I/O under this lock so
    // Save/Save and Save/Load are mutually exclusive at the file level, not
    // just for the in-memory snapshot.
    private readonly object _lock = new();
    private List<ClipItem> _items = new();

    public ItemStore(string dataDir, Func<DateTime>? utcClock = null)
    {
        _dataDir = dataDir;
        _utcClock = utcClock ?? (() => DateTime.UtcNow);
        _itemsPath = Path.Combine(_dataDir, "items.dat");
        _bakPath = Path.Combine(_dataDir, "items.bak");
        _tmpPath = Path.Combine(_dataDir, "items.tmp");
        BlobsDir = Path.Combine(_dataDir, "blobs");

        try
        {
            Directory.CreateDirectory(BlobsDir);
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Error("store", "Failed to create blobs directory", ex);
        }
    }

    public string BlobsDir { get; }

    public IReadOnlyList<ClipItem> Items
    {
        get
        {
            lock (_lock)
            {
                return _items.ToList();
            }
        }
    }

    public event Action? Changed;

    public bool LoadFailed { get; private set; }

    // Cheap pre-check for callers that would otherwise do expensive work (encoding/saving an
    // image blob, for instance) before finding out TryAdd would reject it as a duplicate of the
    // current head anyway. Null when the store is empty. Reads the same _items[0] TryAdd itself
    // compares against, under the same lock, so it can never disagree with what TryAdd decides
    // immediately afterward (single-threaded capture path -- see CapturePipeline).
    public ulong? HeadContentHash
    {
        get
        {
            lock (_lock)
            {
                return _items.Count > 0 ? _items[0].ContentHash : (ulong?)null;
            }
        }
    }

    // Rejects when ContentHash matches the CURRENT HEAD item's hash (items[0] is
    // newest); otherwise inserts at the head. Save is the caller's responsibility.
    public bool TryAdd(ClipItem item)
    {
        lock (_lock)
        {
            if (_items.Count > 0 && _items[0].ContentHash == item.ContentHash)
            {
                return false;
            }

            _items.Insert(0, item);
        }

        Changed?.Invoke();
        return true;
    }

    // 1 path -> single Files item. 2..10 paths -> one Files item. 11+ paths ->
    // multiple Files items, 10 paths each (last chunk holds the remainder).
    // ContentHash is the Fnv1a of the UTF-8 bytes of the paths joined by "\n".
    public static List<ClipItem> BuildFileItems(IReadOnlyList<string> paths, DateTime utcNow)
    {
        var result = new List<ClipItem>();

        for (var offset = 0; offset < paths.Count; offset += 10)
        {
            var chunk = paths.Skip(offset).Take(10).ToList();
            result.Add(new ClipItem
            {
                Kind = ClipKind.Files,
                Paths = chunk,
                CreatedAtUtc = utcNow,
                ContentHash = Fnv1a(Encoding.UTF8.GetBytes(string.Join("\n", chunk)))
            });
        }

        return result;
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            _items.RemoveAll(i => i.Id == id);
        }

        Changed?.Invoke();
    }

    public void RemoveMany(IEnumerable<string> ids)
    {
        var idSet = new HashSet<string>(ids);

        lock (_lock)
        {
            _items.RemoveAll(i => idSet.Contains(i.Id));
        }

        Changed?.Invoke();
    }

    // Filter-driven "clear" support. Pinned items are NOT excluded here; the
    // caller decides whether to keep pinned items out of the predicate.
    public void RemoveAll(Func<ClipItem, bool> predicate)
    {
        lock (_lock)
        {
            _items.RemoveAll(i => predicate(i));
        }

        Changed?.Invoke();
    }

    public void SetPinned(string id, bool pinned)
    {
        lock (_lock)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item != null)
            {
                item.Pinned = pinned;
            }
        }

        Changed?.Invoke();
    }

    public void MoveToTop(string id)
    {
        lock (_lock)
        {
            var index = _items.FindIndex(i => i.Id == id);
            if (index > 0)
            {
                var item = _items[index];
                _items.RemoveAt(index);
                _items.Insert(0, item);
            }
        }

        Changed?.Invoke();
    }

    // Succeeds only when both items are Kind==Files: appends source's paths to
    // target's (duplicates excluded). Fails without changing anything if the
    // combined count would exceed 10. On success, source is removed and
    // target's hash is recomputed.
    public bool TryMergeFiles(string targetId, string sourceId)
    {
        lock (_lock)
        {
            if (targetId == sourceId)
            {
                return false;
            }

            var target = _items.FirstOrDefault(i => i.Id == targetId);
            var source = _items.FirstOrDefault(i => i.Id == sourceId);

            if (target == null || source == null || target.Kind != ClipKind.Files || source.Kind != ClipKind.Files)
            {
                return false;
            }

            var mergedPaths = target.Paths.ToList();
            foreach (var path in source.Paths)
            {
                if (!mergedPaths.Contains(path))
                {
                    mergedPaths.Add(path);
                }
            }

            if (mergedPaths.Count > 10)
            {
                return false;
            }

            target.Paths = mergedPaths;
            target.ContentHash = Fnv1a(Encoding.UTF8.GetBytes(string.Join("\n", mergedPaths)));
            _items.Remove(source);
        }

        Changed?.Invoke();
        return true;
    }

    // Removes path from stack's Paths and creates a new single-file card at
    // the head. Returns null when path isn't present or stack isn't Files.
    // If the stack drops to a single remaining path it stays as its own card.
    public ClipItem? SplitFile(string stackId, string path)
    {
        ClipItem? card = null;

        lock (_lock)
        {
            var stack = _items.FirstOrDefault(i => i.Id == stackId);
            if (stack == null || stack.Kind != ClipKind.Files || !stack.Paths.Contains(path))
            {
                return null;
            }

            stack.Paths = stack.Paths.Where(p => p != path).ToList();
            stack.ContentHash = Fnv1a(Encoding.UTF8.GetBytes(string.Join("\n", stack.Paths)));

            if (stack.Paths.Count == 0)
            {
                _items.Remove(stack);
            }

            card = new ClipItem
            {
                Kind = ClipKind.Files,
                Paths = new List<string> { path },
                CreatedAtUtc = _utcClock(),
                ContentHash = Fnv1a(Encoding.UTF8.GetBytes(path))
            };
            _items.Insert(0, card);
        }

        Changed?.Invoke();
        return card;
    }

    // Deletes unpinned items whose CreatedAtUtc is older than the threshold
    // and returns the count removed. Also deletes Image blob files
    // (ImageFile/ThumbFile in BlobsDir); deletion failures are swallowed
    // (files may be locked) and logged as a warning.
    public int PurgeOlderThan(TimeSpan age)
    {
        List<ClipItem> removed;

        lock (_lock)
        {
            var threshold = _utcClock() - age;
            removed = _items.Where(i => !i.Pinned && i.CreatedAtUtc < threshold).ToList();

            if (removed.Count > 0)
            {
                var removedIds = new HashSet<string>(removed.Select(i => i.Id));
                _items.RemoveAll(i => removedIds.Contains(i.Id));
            }
        }

        foreach (var item in removed)
        {
            DeleteBlobIfPresent(item.ImageFile);
            DeleteBlobIfPresent(item.ThumbFile);
        }

        if (removed.Count > 0)
        {
            Changed?.Invoke();
        }

        return removed.Count;
    }

    private void DeleteBlobIfPresent(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return;
        }

        try
        {
            var path = Path.Combine(BlobsDir, fileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Warn("store", $"Failed to delete blob file '{fileName}': {ex.Message}");
        }
    }

    public void Load()
    {
        lock (_lock)
        {
            if (TryLoadFrom(_itemsPath, out var items))
            {
                _items = items;
                LoadFailed = false;
                return;
            }

            if (TryLoadFrom(_bakPath, out items))
            {
                _items = items;
                LoadFailed = false;
                FileLogger.Instance?.Warn("store", "items.dat was unreadable; recovered from items.bak");
                return;
            }

            _items = new List<ClipItem>();

            if (!File.Exists(_itemsPath) && !File.Exists(_bakPath))
            {
                // Neither file exists: a brand-new install/profile, not a failure. Must not
                // be reported the same as corruption (no ERROR log, no LoadFailed balloon).
                LoadFailed = false;
                FileLogger.Instance?.Info("store", "no existing history; starting fresh");
            }
            else
            {
                LoadFailed = true;
                FileLogger.Instance?.Error("store", "Failed to load items.dat and items.bak; starting with an empty list");
            }
        }
    }

    public void Save()
    {
        // Entire body (snapshot + serialize + encrypt + write + replace) runs
        // under _lock so a concurrent Save() or Load() cannot interleave on
        // the shared items.tmp/.dat/.bak files (see comment on _lock).
        lock (_lock)
        {
            try
            {
                var snapshot = _items.ToList();
                Directory.CreateDirectory(_dataDir);
                var json = JsonSerializer.Serialize(snapshot, JsonOptions);
                var jsonBytes = Encoding.UTF8.GetBytes(json);
                var protectedBytes = ProtectedData.Protect(jsonBytes, null, DataProtectionScope.CurrentUser);

                File.WriteAllBytes(_tmpPath, protectedBytes);

                if (File.Exists(_itemsPath))
                {
                    File.Replace(_tmpPath, _itemsPath, _bakPath);
                }
                else
                {
                    File.Move(_tmpPath, _itemsPath);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Instance?.Error("store", "Failed to save items", ex);
            }
        }
    }

    public static ulong Fnv1a(ReadOnlySpan<byte> data)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var hash = offset;
        foreach (var b in data)
        {
            hash ^= b;
            hash *= prime;
        }

        return hash;
    }

    private static bool TryLoadFrom(string path, out List<ClipItem> items)
    {
        items = new List<ClipItem>();

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var protectedBytes = File.ReadAllBytes(path);
            var jsonBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(jsonBytes);
            var loaded = JsonSerializer.Deserialize<List<ClipItem>>(json, JsonOptions);

            if (loaded == null)
            {
                return false;
            }

            items = loaded;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
