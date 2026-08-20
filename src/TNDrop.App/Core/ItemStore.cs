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

    // Task 4 refines this with full dedup semantics (hash/kind matching, move-to-top, etc.).
    // Minimal form for Task 3: insert at index 0, return true, raise Changed.
    public bool TryAdd(ClipItem item)
    {
        lock (_lock)
        {
            _items.Insert(0, item);
        }

        Changed?.Invoke();
        return true;
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
            LoadFailed = true;
            FileLogger.Instance?.Error("store", "Failed to load items.dat and items.bak; starting with an empty list");
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
