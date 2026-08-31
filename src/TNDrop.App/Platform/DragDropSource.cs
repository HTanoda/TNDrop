using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using TNDrop.Core;
using TNDrop.Services;

// UseWindowsForms is on (for the tray NotifyIcon), so every one of these names is ambiguous
// between the WPF and WinForms flavours. The shelf is WPF: pin them all to System.Windows.
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using DragDrop = System.Windows.DragDrop;
using DragDropEffects = System.Windows.DragDropEffects;

namespace TNDrop.Platform;

/// <summary>
/// Builds the WPF <see cref="DataObject"/> a card hands to Explorer/Word/Paint/... when the user
/// drags it off the shelf, and runs the drag itself.
///
/// <para>The payload for a given <see cref="ClipItem"/> is resolved in exactly one place --
/// <see cref="BuildDataObject"/> and the click-to-copy path in ShelfWindow both go through
/// <see cref="ExistingPaths"/> / <see cref="LoadImage"/>, so "what a drag carries" and "what a
/// click copies" can never quietly disagree.</para>
/// </summary>
public static class DragDropSource
{
    private const string Module = "DragDropSource";

    /// <summary>
    /// Private marker format carried by every DataObject this class builds; the value is the
    /// source <see cref="ClipItem.Id"/>. Drag-IN (Task 13) reads it to recognise -- and ignore --
    /// a card the user dragged out of the shelf and dropped back onto it.
    /// </summary>
    public const string CardIdFormat = "TNDrop.CardId";

    /// <summary>
    /// Private marker format carried ONLY by a single row dragged out of the stack flyout
    /// (Task 14). The value is the source stack's <see cref="ClipItem.Id"/> and the one path,
    /// joined by <see cref="StackPathSeparator"/>.
    ///
    /// <para>Two consumers depend on its presence, not just its value: the split detection needs
    /// the (stack, path) pair to hand to <see cref="ItemStore.SplitFile"/>, and the card-level
    /// merge handler uses "has CardId but NOT this" to tell a whole-card drag from a row drag --
    /// a row must never merge its parent stack into the card it happens to be released over.</para>
    /// </summary>
    public const string StackPathFormat = "TNDrop.StackPath";

    /// <summary>
    /// Separator between the stack id and the path inside a <see cref="StackPathFormat"/> value.
    /// A line feed is not a legal character in a Windows path, so the split can never be ambiguous.
    /// </summary>
    public const char StackPathSeparator = '\n';

    /// <summary>
    /// The item's payload as a drag/drop <see cref="DataObject"/>, or null when there is nothing
    /// left to hand over (empty text, every file path gone, an image whose blob has been deleted).
    /// Pure apart from reading the file system; no UI, no clipboard -- this is the unit under test.
    /// </summary>
    public static DataObject? BuildDataObject(ClipItem item, string blobsDir)
    {
        if (item is null)
        {
            return null;
        }

        switch (item.Kind)
        {
            case ClipKind.Text:
            case ClipKind.Link:
            {
                // v1.8: a text stack has no item.Text -- its content lives in Texts -- so the whole
                // card drags out as every text joined with a newline (design doc §3.3).
                var text = item.IsTextStack ? string.Join("\n", item.Texts) : item.Text;
                if (string.IsNullOrEmpty(text))
                {
                    return null;
                }

                var data = new DataObject();
                data.SetData(DataFormats.UnicodeText, text);
                return Tag(data, item);
            }

            case ClipKind.Files:
            {
                var paths = ExistingPaths(item);
                if (paths.Length == 0)
                {
                    return null;
                }

                var data = new DataObject();
                data.SetData(DataFormats.FileDrop, paths);
                return Tag(data, item);
            }

            case ClipKind.Image:
            {
                // ONE resolution for both formats. Resolving the path and the bitmap separately
                // would let them disagree -- a FileDrop pointing at a blob the bitmap load had
                // already found missing or undecodable -- and the drop target would pick whichever
                // of the two happened to be wrong.
                var (file, bitmap) = ResolveImage(item, blobsDir);

                var data = new DataObject();
                var carried = false;

                // Both formats, deliberately: Explorer/mail clients want a file (CF_HDROP) while
                // Paint/Word/chat apps want the bitmap. Offering both lets one drag satisfy either
                // drop target, and the target picks the format it understands.
                if (file is not null)
                {
                    data.SetData(DataFormats.FileDrop, new[] { file });
                    carried = true;
                }

                if (bitmap is not null)
                {
                    data.SetImage(bitmap);
                    carried = true;
                }

                // Blob gone and not even the thumbnail could be decoded: nothing to drag.
                return carried ? Tag(data, item) : null;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Runs the drag. Returns false without starting one when the item has no payload left (see
    /// <see cref="BuildDataObject"/>) so the caller can tell the user why nothing happened.
    /// <para>Blocks until the drop completes -- <see cref="DragDrop.DoDragDrop"/> pumps its own
    /// message loop -- so the caller is responsible for suspending anything that reacts to the
    /// pointer meanwhile (the shelf's retract countdown; see ShelfWindow._isDragging).</para>
    /// <para>Thin wrapper over the <c>out DragDropEffects</c> overload for callers that do not
    /// care where the payload ended up. ONE implementation, so "what a card drag does" cannot
    /// differ between the two entry points.</para>
    /// </summary>
    public static bool TryStartDrag(FrameworkElement source, ClipItem item, string blobsDir) =>
        TryStartDrag(source, item, blobsDir, out _);

    /// <summary>
    /// <see cref="TryStartDrag(FrameworkElement, ClipItem, string)"/>, additionally reporting what
    /// the drop resolved to.
    /// <para><paramref name="effect"/> is <see cref="DragDropEffects.None"/> for a drag that went
    /// nowhere -- nothing accepted it, the user pressed Esc, or the drag threw -- which is exactly
    /// the half of <see cref="UI.StackGestures.ShouldSplit"/> the shelf's edge-drag extract needs.
    /// It is also None when this returns false (no drag was ever started), so a caller may test it
    /// without first testing the return value only if it has already established there WAS a
    /// payload.</para>
    /// </summary>
    public static bool TryStartDrag(FrameworkElement source, ClipItem item, string blobsDir,
                                    out DragDropEffects effect)
    {
        effect = DragDropEffects.None;

        if (source is null || item is null)
        {
            return false;
        }

        DataObject? data;
        try
        {
            data = BuildDataObject(item, blobsDir);
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Error(Module, "Failed to build the drag payload", ex);
            return false;
        }

        if (data is null)
        {
            FileLogger.Instance?.Warn(Module, $"nothing to drag for a {item.Kind} card (content gone)");
            return false;
        }

        try
        {
            // Link as well as Copy: some targets (shortcut folders, a few editors) only accept a
            // link, and offering it costs nothing -- the shelf keeps its own item either way.
            effect = DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Link);
        }
        catch (Exception ex)
        {
            // A drop target that throws, or a drag cancelled by a shell hiccup, must never take
            // the shelf down with it. Left as None, which is also what the edge-zone extract in
            // ShelfWindow.BeginCardDrag treats as "went nowhere" -- same convention as
            // StackFlyout.BeginRowDrag's own catch.
            FileLogger.Instance?.Error(Module, "Drag operation failed", ex);
        }

        return true;
    }

    /// <summary>
    /// Thin wrapper over <see cref="TryStartDrag"/> that discards the "had anything to drag"
    /// answer. Same resolution, same behaviour -- callers that want to report the empty case to
    /// the user should use <see cref="TryStartDrag"/>.
    /// </summary>
    public static void StartDrag(FrameworkElement source, ClipItem item, string blobsDir) =>
        TryStartDrag(source, item, blobsDir);

    /// <summary>
    /// The payload for ONE path dragged out of a stack's flyout (Task 14): an ordinary single-file
    /// CF_HDROP -- so dropping it into Explorer/Word behaves exactly like dragging that file from
    /// anywhere else -- plus the two TNDrop markers. Null when the path is not part of
    /// <paramref name="stack"/> or is no longer on disk, so the caller can say why nothing happened
    /// rather than starting a drag that carries an empty list.
    /// </summary>
    public static DataObject? BuildStackRowDataObject(ClipItem? stack, string? path)
    {
        if (stack is null || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // v1.8: テキストスタックの行はテキストとして持ち出す。membership 再確認の理由は
        // ファイル行と同じ (古いフライアウトの行を掴んだケース)。
        if (stack.IsTextStack)
        {
            if (!stack.Texts.Contains(path))
            {
                FileLogger.Instance?.Warn(Module, "refused to drag a row that is not part of the stack anymore");
                return null;
            }

            var textData = new DataObject();
            textData.SetData(DataFormats.UnicodeText, path);
            return Tag(textData, stack);
        }

        // Membership is checked here as well as in ItemStore.SplitFile: this is what stops a stale
        // flyout (rows built before a background change) from dragging a path the stack no longer
        // holds and then splitting it back in as a new card.
        if (stack.Kind != ClipKind.Files || !stack.Paths.Contains(path))
        {
            FileLogger.Instance?.Warn(Module, "refused to drag a row that is not part of the stack anymore");
            return null;
        }

        if (!PathExists(path))
        {
            FileLogger.Instance?.Warn(Module, "nothing to drag for a stacked file (gone from disk)");
            return null;
        }

        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { path });
        data.SetData(StackPathFormat, EncodeStackPath(stack.Id ?? string.Empty, path));
        return Tag(data, stack);
    }

    /// <summary>The <see cref="StackPathFormat"/> value for a (stack, path) pair.</summary>
    public static string EncodeStackPath(string stackId, string path) =>
        (stackId ?? string.Empty) + StackPathSeparator + (path ?? string.Empty);

    /// <summary>
    /// Inverse of <see cref="EncodeStackPath"/>. False (with both outputs empty) for anything that
    /// is not a well-formed pair, so a malformed marker can never be turned into a split of ""
    /// out of stack "".
    /// </summary>
    public static bool TryDecodeStackPath(string? encoded, out string stackId, out string path)
    {
        stackId = string.Empty;
        path = string.Empty;

        if (string.IsNullOrEmpty(encoded))
        {
            return false;
        }

        var separator = encoded.IndexOf(StackPathSeparator);
        if (separator <= 0 || separator == encoded.Length - 1)
        {
            return false;
        }

        stackId = encoded[..separator];
        path = encoded[(separator + 1)..];
        return true;
    }

    /// <summary>
    /// The item's file paths that still exist on disk, in their original order. Directories count:
    /// a folder copied to the clipboard is a legitimate CF_HDROP entry.
    /// </summary>
    public static string[] ExistingPaths(ClipItem item)
    {
        if (item?.Paths is null)
        {
            return Array.Empty<string>();
        }

        return item.Paths
            .Where(p => !string.IsNullOrWhiteSpace(p) && PathExists(p))
            .ToArray();
    }

    /// <summary>
    /// The one definition of "this clipboard path is still there" used across the shelf -- the drag
    /// payload, click-to-copy, and the stack flyout's rows all go through it, so they cannot
    /// disagree about which of a stack's files are live. Directories count: a folder is a
    /// legitimate CF_HDROP entry.
    /// </summary>
    public static bool PathExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (Exception ex)
        {
            // A path on a disconnected network share can throw rather than return false.
            // Never log the path itself or ex.Message (which can embed it) -- clipboard-derived
            // paths are user content and must not land in logs/app-YYYYMMDD.log.
            FileLogger.Instance?.Warn(Module, $"existence check failed: {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// Resolves an Image item's payload -- the full-size blob path AND the bitmap -- in one pass.
    /// This is the single source of truth both <see cref="FullImagePath"/> and
    /// <see cref="LoadImage"/> are thin wrappers over, so the two can never disagree.
    /// <para>The path is offered only when the very same file also decoded: a blob that is missing
    /// or corrupt is no more useful to a drop target as a file than it is as a bitmap. When it is
    /// unusable the thumbnail stands in for the bitmap (a downscaled copy still beats handing the
    /// user nothing) and no path is offered at all -- the thumbnail is not the image the card
    /// promises.</para>
    /// </summary>
    public static (string? FullPath, BitmapSource? Bitmap) ResolveImage(ClipItem item, string blobsDir)
    {
        if (item is null)
        {
            return (null, null);
        }

        var fullPath = BlobPath(item.ImageFile, blobsDir);
        var bitmap = Decode(fullPath);
        if (bitmap is not null)
        {
            return (fullPath, bitmap);
        }

        var thumb = Decode(BlobPath(item.ThumbFile, blobsDir));
        if (thumb is not null)
        {
            // Worth a line in the log: what the user gets from here on is the 320px-wide
            // thumbnail, not the original, and nothing on screen says so.
            FileLogger.Instance?.Warn(Module,
                $"full-size blob unusable for image item {item.Id}; falling back to the thumbnail");
        }

        return (null, thumb);
    }

    /// <summary>
    /// Full path of the item's full-size image blob, or null when it is missing or unusable.
    /// Thin wrapper over <see cref="ResolveImage"/>.
    /// </summary>
    public static string? FullImagePath(ClipItem item, string blobsDir) =>
        ResolveImage(item, blobsDir).FullPath;

    /// <summary>
    /// The item's image as a frozen <see cref="BitmapSource"/> for CF_BITMAP / the clipboard.
    /// Thin wrapper over <see cref="ResolveImage"/>.
    /// </summary>
    public static BitmapSource? LoadImage(ClipItem item, string blobsDir) =>
        ResolveImage(item, blobsDir).Bitmap;

    /// <summary>
    /// v1.3 Task B review fix: prepares BOTH sides of a card-to-card merge for
    /// <see cref="ItemStore.TryMergeFiles"/>, converting whichever side(s) are currently
    /// Kind==Image to Kind==Files first -- the one place this happens, so a caller (ShelfWindow's
    /// drop handler) never has to get the ordering right itself.
    ///
    /// <para><b>Why this exists as one call instead of two.</b> The original v1.3 Task B code
    /// converted <paramref name="target"/>, then checked whether <paramref name="source"/> could
    /// also convert -- so a healthy target sitting next to a source whose blob had gone missing
    /// (deleted externally, quarantined by AV, a prior crash) was left PERMANENTLY converted even
    /// though the merge was refused: its Title silently changed from blank to a raw file name and
    /// its click-to-copy silently changed from SetImage to SetFiles. This resolves BOTH sides'
    /// blob paths first (<see cref="FullImagePath"/>, read-only, no store mutation) and only
    /// converts either one once BOTH are confirmed resolvable -- so a refusal here is a true no-op,
    /// exactly like the pre-existing "10-file cap" refusal in <see cref="ItemStore.TryMergeFiles"/>
    /// already was.</para>
    ///
    /// <para>Returns false, with NEITHER card mutated, when an Image side's blob is missing or
    /// undecodable (the same case <see cref="BuildDataObject"/> itself refuses to hand over for a
    /// drag). Already-Files cards on either side pass through untouched. Does not itself call
    /// <see cref="ItemStore.TryMergeFiles"/> -- the caller still does that afterward, the same
    /// as before this fix, so the 10-file cap's own independent refusal (and its own "nothing
    /// mutated" guarantee) is unaffected.</para>
    /// </summary>
    public static bool TryPrepareCardsForMerge(ItemStore store, string blobsDir, ClipItem target, ClipItem source)
    {
        if (!TryResolveImagePath(target, blobsDir, out var targetPath))
        {
            return false;
        }

        if (!TryResolveImagePath(source, blobsDir, out var sourcePath))
        {
            return false;
        }

        // Both sides are known-resolvable now -- only past this point does either card get
        // mutated.
        if (target.Kind == ClipKind.Image && store.ConvertImageToFileCard(target.Id, targetPath) is null)
        {
            return false;
        }

        if (source.Kind == ClipKind.Image && store.ConvertImageToFileCard(source.Id, sourcePath) is null)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Read-only half of <see cref="TryPrepareCardsForMerge"/>: true (with a null path) for a
    /// non-Image card -- nothing to resolve -- or true (with the resolved path) for an Image card
    /// whose blob is usable. False only when the card is Image and <see cref="FullImagePath"/>
    /// comes back null. Never touches the store.
    /// </summary>
    private static bool TryResolveImagePath(ClipItem card, string blobsDir, out string? path)
    {
        if (card.Kind != ClipKind.Image)
        {
            path = null;
            return true;
        }

        path = FullImagePath(card, blobsDir);
        return path is not null;
    }

    private static DataObject Tag(DataObject data, ClipItem item)
    {
        // Always present, on every kind: Task 13's drop handler uses it to tell "the user dragged
        // this card out of the shelf and back in" from a genuine external drop.
        data.SetData(CardIdFormat, item.Id ?? string.Empty);
        return data;
    }

    private static string? BlobPath(string? blobFileName, string blobsDir)
    {
        if (string.IsNullOrWhiteSpace(blobFileName) || string.IsNullOrWhiteSpace(blobsDir))
        {
            return null;
        }

        try
        {
            var path = Path.Combine(blobsDir, blobFileName);
            return File.Exists(path) ? path : null;
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Warn(Module, $"blob path resolution failed for '{blobFileName}': {ex.Message}");
            return null;
        }
    }

    private static BitmapSource? Decode(string? path)
    {
        if (path is null)
        {
            return null;
        }

        try
        {
            // OnLoad + our own stream: the decoder must not keep the blob file open, or a later
            // purge/delete of that blob would fail while the shelf is running.
            using var stream = File.OpenRead(path);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Warn(Module, $"failed to decode image blob '{path}': {ex.Message}");
            return null;
        }
    }
}
