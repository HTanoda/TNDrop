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
    // internal (v1.6 Task 5): BackupService deserializes/reserializes the SAME List<ClipItem>
    // shape during an import's blob-path rewrite, so it must use these exact options (the
    // JsonStringEnumConverter in particular -- a second options instance without it would write
    // Kind as a number and silently produce an items.dat this store cannot read back). One
    // definition, shared, rather than two that can drift apart.
    internal static readonly JsonSerializerOptions JsonOptions = new()
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

    // Fires once per successful Save(), OUTSIDE _lock. lock/Monitor is re-entrant on the same
    // thread, so a handler calling back into CopyDataTo/ReadDecryptedJson would not deadlock
    // even if raised from inside the lock -- the real reasons to raise outside are (1) a handler
    // is arbitrary code we don't control, and one that blocks waiting on ANOTHER thread that
    // itself needs _lock (e.g. a Dispatcher.Invoke back onto the UI thread while that thread is
    // blocked on _lock) genuinely can deadlock, and (2) holding _lock across a slow handler (a
    // multi-file backup copy) would stall every other Save/Load/TryAdd on the capture path behind
    // it. This is the daily-auto-backup trigger Task 5's BackupService subscribes to; it never
    // fires when Save() catches and logs a failure.
    public event Action? Saved;

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

    // Removes the item and, if it was an Image, its ImageFile/ThumbFile blobs from disk --
    // same blob-deletion path PurgeOlderThan uses, so a user-initiated delete is a real delete
    // (see DeleteBlobsFor).
    public void Remove(string id)
    {
        List<ClipItem> removed;

        lock (_lock)
        {
            removed = _items.Where(i => i.Id == id).ToList();
            _items.RemoveAll(i => i.Id == id);
        }

        DeleteBlobsFor(removed);
        Changed?.Invoke();
    }

    // Batch counterpart of Remove -- same blob cleanup, for RemoveSelected/multi-select delete.
    public void RemoveMany(IEnumerable<string> ids)
    {
        var idSet = new HashSet<string>(ids);
        List<ClipItem> removed;

        lock (_lock)
        {
            removed = _items.Where(i => idSet.Contains(i.Id)).ToList();
            _items.RemoveAll(i => idSet.Contains(i.Id));
        }

        DeleteBlobsFor(removed);
        Changed?.Invoke();
    }

    // Filter-driven "clear" support. Pinned items are NOT excluded here; the
    // caller decides whether to keep pinned items out of the predicate. Same blob cleanup as
    // Remove/RemoveMany -- predicate is invoked twice (selection snapshot, then RemoveAll's own
    // scan) so it must stay a pure read of the item, which every current caller already is.
    public void RemoveAll(Func<ClipItem, bool> predicate)
    {
        List<ClipItem> removed;

        lock (_lock)
        {
            removed = _items.Where(i => predicate(i)).ToList();
            _items.RemoveAll(i => predicate(i));
        }

        DeleteBlobsFor(removed);
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
    //
    // Pinning is OR-ed, not inherited from the target. A merge deletes the
    // source item, so a pinned source folded into an unpinned target would
    // silently hand its files to a card PurgeOlderThan is free to delete --
    // the user pinned those paths, and dragging one card onto another is not
    // a request to unprotect them.
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
            target.Pinned |= source.Pinned;
            _items.Remove(source);
        }

        Changed?.Invoke();
        return true;
    }

    // Converts an Image item in place into the single-path Kind=Files card TryMergeFiles requires,
    // so a clipboard screenshot can join a merge exactly like any other file card (v1.3 Task B).
    // `filePath` is resolved by the CALLER via Platform.DragDropSource.FullImagePath -- the SAME
    // function drag-out uses to decide what file a card hands to Explorer -- so "what file this
    // Image card materializes as" is never answered twice (one-resolution). Returns null (no
    // mutation) when the item is missing, is not Kind==Image, or filePath is empty (the caller
    // already found nothing draggable, mirroring DragDropSource's own "nothing to drag" refusal).
    //
    // Id/Pinned/CreatedAtUtc are left untouched, so a merge that follows keeps inheriting them the
    // same way TryMergeFiles already does for two Files cards. ImageFile/ThumbFile are cleared so
    // the persisted card carries no Image-specific fields once Kind==Files. The now-orphaned
    // thumbnail blob -- no longer reachable from anywhere, since CardViewModel only reads ThumbFile
    // for Kind==Image -- is deleted right here through the same DeleteBlobIfPresent path every
    // other blob cleanup uses, rather than left to leak (nothing will ever reference it again).
    //
    // v1.3 Task B review fix: the blob is also RENAMED, on disk, to BlobNaming's human-meaningful
    // name here -- the ONE moment "materialize as a Files card" happens -- so the file on disk,
    // this card's Paths entry, drag-out, and every display (Title/Subtitle/flyout rows) agree, and
    // the card never again shows a bare GUID filename or leaks the blobs\ path as if it were the
    // user's own file. A rename failure (locked file, path too long) falls back to `filePath`
    // unchanged -- a naming cosmetic must never block the conversion itself.
    public ClipItem? ConvertImageToFileCard(string itemId, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        ClipItem? item;
        string? orphanedThumb;

        lock (_lock)
        {
            item = _items.FirstOrDefault(i => i.Id == itemId);
            if (item == null || item.Kind != ClipKind.Image)
            {
                return null;
            }

            orphanedThumb = item.ThumbFile;
            var finalPath = RenameToFriendlyName(filePath!, item.CreatedAtUtc);

            item.Kind = ClipKind.Files;
            item.Paths = new List<string> { finalPath };
            item.ContentHash = Fnv1a(Encoding.UTF8.GetBytes(finalPath));
            item.ImageFile = null;
            item.ThumbFile = null;
        }

        DeleteBlobIfPresent(orphanedThumb);
        Changed?.Invoke();
        return item;
    }

    // Renames the just-resolved blob (still its capture-time GUID name) to
    // BlobNaming.FriendlyImageFileName's name, in the same directory, and returns the new full
    // path -- or the original `currentFullPath` unchanged when there is no directory component or
    // the actual File.Move fails (logged and swallowed, same as every other blob-touching catch in
    // this file). Called from inside ConvertImageToFileCard's own `lock (_lock)`, same as Save()
    // runs its entire file I/O under _lock (see the remarks on Save) -- doing the rename under the
    // same lock that validated Kind==Image is what stops a concurrent Remove/merge from racing
    // with a half-renamed blob; this file already accepts holding _lock across real disk I/O as
    // its established pattern, not a new one.
    private string RenameToFriendlyName(string currentFullPath, DateTime createdAtUtc)
    {
        var directory = Path.GetDirectoryName(currentFullPath);
        if (string.IsNullOrEmpty(directory))
        {
            return currentFullPath;
        }

        try
        {
            var friendlyName = BlobNaming.FriendlyImageFileName(createdAtUtc,
                candidate => File.Exists(Path.Combine(directory, candidate)));
            var newFullPath = Path.Combine(directory, friendlyName);

            if (string.Equals(newFullPath, currentFullPath, StringComparison.OrdinalIgnoreCase))
            {
                return currentFullPath;
            }

            File.Move(currentFullPath, newFullPath);
            return newFullPath;
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Warn("store", $"Failed to rename blob to a friendly name: {ex.GetType().Name}");
            return currentFullPath;
        }
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
                ContentHash = Fnv1a(Encoding.UTF8.GetBytes(path)),
                Pinned = stack.Pinned
            };
            _items.Insert(0, card);
        }

        Changed?.Invoke();
        return card;
    }

    // Explicit-UI counterpart of dragging every row out of a stack one at a time (v1.3 Task C):
    // fully expands a Files stack into one single-path card per path, in a single Changed
    // notification. Reuses SplitFile's own "new card, Pinned inherited from the stack" rule
    // rather than a second version of it -- a caller with just one file to peel off still goes
    // through SplitFile untouched; this only differs in expanding ALL paths and always removing
    // the original stack (SplitFile lets a >=2-path remainder survive as itself, but SplitAll's
    // whole point is that nothing remains grouped).
    //
    // Blob ownership (v1.3 Task B's blob-in-Paths convention) needs no separate handling here:
    // each path string is moved -- not copied -- from the stack's Paths into exactly one new
    // card's Paths, the same "exactly one live card references a given blob file" invariant
    // DeleteBlobsFor's remarks describe for SplitFile.
    //
    // Returns null (no mutation, no Changed) when stackId does not resolve to a Files stack with
    // 2+ paths -- IsStack is the SAME single-resolution check CardViewModel.IsStack and the
    // ungroup-all UI both read, so a lone file or a non-Files card is refused here exactly where
    // the UI would already have hidden the affordance.
    public List<ClipItem>? SplitAll(string stackId)
    {
        List<ClipItem> created;

        lock (_lock)
        {
            var stack = _items.FirstOrDefault(i => i.Id == stackId);
            if (stack == null || !stack.IsStack)
            {
                return null;
            }

            var paths = stack.Paths.ToList();
            _items.Remove(stack);

            created = new List<ClipItem>();
            // Insert at an advancing index (0, 1, 2, ...) rather than always at 0 -- a fixed
            // Insert(0, card) in this loop would land paths [a,b,c] as shelf order [c,b,a] (each
            // insert pushes the previous one back), silently reversing the flyout order the user
            // just saw. Advancing the index keeps each new card immediately after the one before
            // it, so the shelf ends up [a,b,c] at the head of the list -- same order as paths.
            var insertIndex = 0;
            foreach (var path in paths)
            {
                var card = new ClipItem
                {
                    Kind = ClipKind.Files,
                    Paths = new List<string> { path },
                    CreatedAtUtc = _utcClock(),
                    ContentHash = Fnv1a(Encoding.UTF8.GetBytes(path)),
                    Pinned = stack.Pinned
                };
                _items.Insert(insertIndex, card);
                insertIndex++;
                created.Add(card);
            }
        }

        Changed?.Invoke();
        return created;
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

        DeleteBlobsFor(removed);

        if (removed.Count > 0)
        {
            Changed?.Invoke();
        }

        return removed.Count;
    }

    // Keeps the newest `max` UNPINNED items and removes any older unpinned ones beyond that;
    // pinned items are never counted against the cap and never removed here, matching
    // PurgeOlderThan's own "pinned is protected" contract. _items is newest-first, so walking it
    // in order and counting only unpinned items as they're seen naturally keeps the newest ones
    // first and marks the rest (regardless of how many pinned items are interleaved between
    // them) for removal. Blob cleanup goes through the same DeleteBlobsFor path as
    // Remove/RemoveMany/RemoveAll/PurgeOlderThan; Changed is raised once, only when something was
    // actually removed. Called by CapturePipeline after a successful TryAdd and before Save, with
    // the raw (Func-injected) AppSettings.HistoryCapacity value -- this method does not itself
    // clamp `max` into [Min,Max]Capacity (SettingsStore.Load does that for the persisted setting),
    // so a caller can still ask for an arbitrarily small or large cap, which is what the tests use.
    public int TrimUnpinnedToCapacity(int max)
    {
        var capacity = Math.Max(0, max);
        List<ClipItem> removed;

        lock (_lock)
        {
            var keptUnpinned = 0;
            removed = new List<ClipItem>();

            foreach (var item in _items)
            {
                if (item.Pinned)
                {
                    continue;
                }

                if (keptUnpinned < capacity)
                {
                    keptUnpinned++;
                }
                else
                {
                    removed.Add(item);
                }
            }

            if (removed.Count > 0)
            {
                var removedIds = new HashSet<string>(removed.Select(i => i.Id));
                _items.RemoveAll(i => removedIds.Contains(i.Id));
            }
        }

        DeleteBlobsFor(removed);

        if (removed.Count > 0)
        {
            Changed?.Invoke();
        }

        return removed.Count;
    }

    // Single blob-deletion path shared by Remove/RemoveMany/RemoveAll/PurgeOlderThan: any code
    // path that drops items from _items routes their Image blobs through here so a delete is
    // never "removed from the list but still on disk". Non-Image items have no ImageFile/
    // ThumbFile and are no-ops for those two calls.
    //
    // v1.3 Task B: a Kind==Files card can now hold a path inside BlobsDir too (an Image card
    // converted via ConvertImageToFileCard, alone or folded into a stack by TryMergeFiles), so its
    // Paths are checked the same way. SplitFile moves a blob path from one card's Paths to
    // another's rather than copying it, so at any point exactly one live card's Paths references a
    // given blob file -- deleting THAT card is what deletes the blob, and deleting any other card
    // never can (no orphan, no double-delete).
    private void DeleteBlobsFor(List<ClipItem> items)
    {
        foreach (var item in items)
        {
            DeleteBlobIfPresent(item.ImageFile);
            DeleteBlobIfPresent(item.ThumbFile);

            if (item.Kind == ClipKind.Files)
            {
                foreach (var path in item.Paths)
                {
                    DeleteBlobPathIfUnderBlobsDir(path);
                }
            }
        }
    }

    // Deletes `path` ONLY when it resolves (after full-path normalization) to somewhere inside
    // BlobsDir. A plain string prefix check on the raw path would be wrong on Windows: paths are
    // case-insensitive, can carry "." segments or mixed slashes, and a naive prefix test would also
    // match a sibling directory whose name merely starts with "blobs" (e.g. "blobsEvil") -- so both
    // sides go through Path.GetFullPath and the comparison is case-insensitive with an explicit
    // trailing separator appended to the blobs side before the StartsWith check. Anything outside
    // BlobsDir -- every ordinary user file a ClipKind.Files card points at -- is left completely
    // untouched; this must never become a general "delete files behind a deleted card" feature.
    private void DeleteBlobPathIfUnderBlobsDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string fullPath;
        string blobsRoot;

        try
        {
            fullPath = Path.GetFullPath(path);
            blobsRoot = Path.GetFullPath(BlobsDir);
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Warn("store", $"blob-path containment check failed: {ex.GetType().Name}");
            return;
        }

        var blobsRootWithSeparator = blobsRoot.EndsWith(Path.DirectorySeparatorChar)
            ? blobsRoot
            : blobsRoot + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(blobsRootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Warn("store", $"Failed to delete blob path: {ex.GetType().Name}");
        }
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
            FileLogger.Instance?.Warn("store", $"Failed to delete blob file: {ex.GetType().Name}");
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
        var ok = false;

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

                ok = true;
            }
            catch (Exception ex)
            {
                FileLogger.Instance?.Error("store", "Failed to save items", ex);
            }
        }

        // Raised outside _lock -- see Saved's remarks: the risk isn't same-thread re-entrancy
        // (lock is re-entrant) but an arbitrary handler blocking on another thread that needs
        // _lock, or a slow handler stalling the next Save/Load/TryAdd behind it.
        if (ok)
        {
            Saved?.Invoke();
        }
    }

    // Copies the current items.dat (or items.bak when items.dat is gone -- see below) and every
    // file in BlobsDir into destDir, for backup
    // export. Runs entirely under _lock so it sees a consistent on-disk snapshot with respect to
    // a concurrent Save()/Load()/ReplaceDataFrom -- same "hold _lock across real disk I/O"
    // pattern Save() itself uses. destDir (and destDir\blobs) are created if missing. Blob files
    // are copied flat (BlobsDir never contains subdirectories -- see DeleteBlobsFor's remarks on
    // the blob-ownership invariant), each overwriting any same-named file already at the
    // destination.
    public void CopyDataTo(string destDir)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(destDir);

            // items.dat が無いときは items.bak を items.dat として出す -- 解決は
            // ResolveCurrentItemsPath 1 箇所 (Load() のフォールバックと同じ答え) に任せる。
            // Save() の File.Replace 中のクラッシュや items.dat の外部削除で .bak しか残って
            // いない状態でバックアップを取ると、これが無ければ「履歴 0 件のバックアップ」を
            // 作ってしまい、それを使った巻き戻しが Load() なら復旧できたはずの履歴を静かに消す
            // (v1.6 Task 5 レビュー修正)。.bak も items.dat という名前で置くのは、復元側
            // (ReplaceDataFrom) と BackupService.Validate が items.dat だけを見るため。
            var currentItemsPath = ResolveCurrentItemsPath();
            if (currentItemsPath is not null)
            {
                File.Copy(currentItemsPath, Path.Combine(destDir, "items.dat"), overwrite: true);

                if (!string.Equals(currentItemsPath, _itemsPath, StringComparison.OrdinalIgnoreCase))
                {
                    FileLogger.Instance?.Warn("store", "items.dat missing; backed up items.bak instead");
                }
            }

            var destBlobsDir = Path.Combine(destDir, "blobs");
            Directory.CreateDirectory(destBlobsDir);

            // BlobsDir is created best-effort in the constructor (its own try/catch just logs on
            // failure), so it is not guaranteed to exist here -- Directory.GetFiles throws
            // DirectoryNotFoundException on a missing directory, which would turn a legitimately
            // empty/never-created blobs dir into a hard failure of the whole copy.
            Directory.CreateDirectory(BlobsDir);

            foreach (var blobPath in Directory.GetFiles(BlobsDir))
            {
                File.Copy(blobPath, Path.Combine(destBlobsDir, Path.GetFileName(blobPath)), overwrite: true);
            }
        }
    }

    // Returns the plaintext JSON the current items file decrypts to (DPAPI, CurrentUser scope --
    // same protection TryLoadFrom/Save use), or null when there is no history file at all. Used by
    // Task 5's BackupService to embed the current history into an export container without going
    // through a second serialize of _items (this reads the file Save() already wrote, rather than
    // re-serializing the in-memory snapshot, so an export always reflects exactly what is on
    // disk). A decrypt failure is NOT swallowed here -- it propagates to the caller, unlike
    // Load()'s "unreadable means empty" tolerance, because a caller asking to read out an
    // existing file for export/inspection needs to know it failed, not silently get null.
    //
    // v1.6 最終レビュー修正 (Fix 3): 「どのファイルが現在の履歴か」は Load() と同じ
    // ResolveCurrentItemsPath に聞く。items.dat が無く items.bak だけがある状態 (Load() が
    // 復旧できる状態、CopyDataTo もフォールバックする状態) でここだけ null を返すと、
    // BackupService.ExportTo がその null を "[]" に潰し、**復旧可能な履歴が空のエクスポート
    // ファイルになる** -- それを取り込んだ移行先は履歴を失う。
    public string? ReadDecryptedJson()
    {
        lock (_lock)
        {
            var path = ResolveCurrentItemsPath();
            if (path is null)
            {
                return null;
            }

            if (!string.Equals(path, _itemsPath, StringComparison.OrdinalIgnoreCase))
            {
                FileLogger.Instance?.Warn("store", "items.dat missing; read items.bak instead");
            }

            var protectedBytes = File.ReadAllBytes(path);
            var jsonBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(jsonBytes);
        }
    }

    // 「Load() なら今どのファイルを履歴として読むか」の唯一の解決点: items.dat があればそれ、
    // 無ければ items.bak (Load() の復旧フォールバックと同じ順序)、どちらも無ければ null。
    // CopyDataTo (バックアップ) と ReadDecryptedJson (エクスポート) はどちらもこれを使うので、
    // 「バックアップは .bak を拾うのにエクスポートは空になる」という食い違いが構造上作れない。
    // 呼び出し側は _lock の中から呼ぶこと (ファイルの有無を見てから読むまでの間に Save() が
    // 割り込むと答えが変わるため)。
    private string? ResolveCurrentItemsPath()
    {
        if (File.Exists(_itemsPath))
        {
            return _itemsPath;
        }

        return File.Exists(_bakPath) ? _bakPath : null;
    }

    // Encrypts `json` with DPAPI (CurrentUser scope, matching Save()/TryLoadFrom) and writes it
    // to `path`. Used to stage a restored/imported items.dat before ReplaceDataFrom picks it up,
    // and by tests to build a roundtrip fixture -- it is the write-side counterpart of
    // ReadDecryptedJson, so together they let a caller round-trip an ItemStore's on-disk content
    // through plain JSON without ever touching a live store's _items list.
    public static void WriteEncryptedJson(string path, string json)
    {
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, protectedBytes);
    }

    // Restore-time pre-check: can THIS Windows user account's DPAPI decrypt itemsDatPath at all?
    // DPAPI keys are per-user, so a items.dat copied from another account (or corrupted) fails
    // here before ReplaceDataFrom ever touches the live store. Any failure (bad DPAPI blob,
    // missing/unreadable file) collapses to false -- callers only need a yes/no gate, not the
    // distinction between "wrong user" and "corrupt bytes".
    public static bool CanDecrypt(string itemsDatPath)
    {
        try
        {
            ProtectedData.Unprotect(File.ReadAllBytes(itemsDatPath), null, DataProtectionScope.CurrentUser);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Restores this store's on-disk files AND in-memory items from srcDir (a backup/import
    // staging directory laid out like _dataDir itself: items.dat + blobs\). Validation happens
    // FIRST, before any current file is touched: if srcDir\items.dat exists but fails to
    // decrypt/deserialize, this throws InvalidDataException and leaves the live store completely
    // untouched. This intentionally does NOT reuse Load()'s "unreadable means start empty"
    // tolerance -- that tolerance is right for first-run/corruption at startup, but reused here it
    // would make restoring a broken backup silently wipe the user's history instead of failing
    // loudly; Task 5's rollback path relies on catching this exception before anything changes.
    // The same TryLoadFrom() call that validates srcDir's items.dat also produces the item list
    // used for the in-memory swap below -- one parse, not a validate-then-reload-again pair.
    //
    // items.dat is swapped the SAME way Save() writes it -- via _tmpPath then File.Replace (or
    // File.Move when there is no existing items.dat) -- rather than delete-then-copy. A
    // delete-then-copy sequence has a window, after items.bak is deleted and before the new file
    // lands, where a crash leaves NEITHER a live items.dat NOR a recovery .bak: the next Load()
    // would then report a fresh install and the user's whole history is silently gone. The atomic
    // swap never deletes _bakPath itself; when srcDir has no items.dat (restoring to empty
    // history), only the live _itemsPath is deleted and the existing _bakPath is left in place as
    // the recovery copy.
    //
    // Caller contract: an InvalidDataException means nothing on disk or in memory was touched.
    // Any OTHER exception thrown after the items.dat swap below leaves items.dat and _items
    // consistent with each other (they are always updated together, before any blob I/O runs) but
    // the blobs\ directory's contents are indeterminate -- blob delete/copy is best-effort
    // (per-file catch-and-warn, matching DeleteBlobIfPresent's convention elsewhere in this file)
    // specifically so that one locked/inaccessible blob file cannot leave items.dat and _items
    // out of sync with each other, at the cost of a possible partial blobs\ swap the caller may
    // need to re-run or report.
    public void ReplaceDataFrom(string srcDir)
    {
        var srcItemsPath = Path.Combine(srcDir, "items.dat");

        lock (_lock)
        {
            List<ClipItem> newItems;

            if (File.Exists(srcItemsPath))
            {
                if (!TryLoadFrom(srcItemsPath, out newItems))
                {
                    throw new InvalidDataException(
                        $"Restore source items.dat could not be decrypted/parsed: {srcItemsPath}");
                }
            }
            else
            {
                newItems = new List<ClipItem>();
            }

            // Atomic items.dat swap, same shape as Save(): stage into _tmpPath, then
            // File.Replace (existing items.dat -> becomes the new .bak) or File.Move (no existing
            // items.dat). _bakPath is never deleted here -- see the remarks above.
            if (File.Exists(srcItemsPath))
            {
                File.Copy(srcItemsPath, _tmpPath, overwrite: true);

                if (File.Exists(_itemsPath))
                {
                    File.Replace(_tmpPath, _itemsPath, _bakPath);
                }
                else
                {
                    File.Move(_tmpPath, _itemsPath);
                }
            }
            else if (File.Exists(_itemsPath))
            {
                File.Delete(_itemsPath);
            }

            // Commit the in-memory swap immediately after the on-disk items.dat swap, and BEFORE
            // any blob I/O -- see the caller-contract remarks above.
            _items = newItems;

            // BlobsDir is created best-effort in the constructor, so it is not guaranteed to
            // exist here (see CopyDataTo's matching comment).
            Directory.CreateDirectory(BlobsDir);

            foreach (var existingBlob in Directory.GetFiles(BlobsDir))
            {
                try
                {
                    File.Delete(existingBlob);
                }
                catch (Exception ex)
                {
                    FileLogger.Instance?.Warn("store", $"Failed to delete blob during restore: {ex.GetType().Name}");
                }
            }

            var srcBlobsDir = Path.Combine(srcDir, "blobs");
            if (Directory.Exists(srcBlobsDir))
            {
                foreach (var blobPath in Directory.GetFiles(srcBlobsDir))
                {
                    try
                    {
                        File.Copy(blobPath, Path.Combine(BlobsDir, Path.GetFileName(blobPath)), overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Instance?.Warn("store", $"Failed to copy blob during restore: {ex.GetType().Name}");
                    }
                }
            }
        }

        // Raised outside _lock, matching every other mutator in this file (TryAdd/Remove/etc.).
        Changed?.Invoke();
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
