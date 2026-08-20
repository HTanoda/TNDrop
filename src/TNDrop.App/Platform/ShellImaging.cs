using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TNDrop.Services;

namespace TNDrop.Platform;

/// <summary>
/// Shell-provided artwork for a file system path: the large preview Explorer shows for an image
/// or a video (<see cref="GetThumbnail"/>), and the small per-extension icon it shows for
/// everything else (<see cref="GetIcon"/>). Both go through IShellItemImageFactory, so whatever
/// thumbnail handler is registered on the machine (Office, a codec pack, a scanner suite) is used
/// -- TNDrop never decodes the file itself.
///
/// <para><b>Threading.</b> Intended to be called from the WPF UI thread (an STA), which is where
/// the lazy card view-model properties run. The caches are lock-guarded so a background caller
/// cannot corrupt them, but the COM calls themselves are left to the shell's own apartment rules;
/// nothing here promises good behaviour from an MTA thread.</para>
///
/// <para><b>Failure policy.</b> Every failure path returns null -- a missing file, an unregistered
/// handler, a handler that throws, a shell item that cannot be parsed. Nothing throws out of this
/// class. Failures are logged at most once per <see cref="FailureLogInterval"/> and carry only the
/// operation name and the exception type: never the path, never the exception message, since a
/// path is user content and this log is shipped to a shared folder.</para>
/// </summary>
public static class ShellImaging
{
    private const string Module = "ShellImaging";

    /// <summary>
    /// Entries per cache (icons and thumbnails are capped separately, so the worst case is 256
    /// live <see cref="ImageSource"/>s). Icons are tiny; thumbnails at 256px are ~256KB each, so
    /// a full thumbnail cache is on the order of 32MB -- the point of the cap.
    /// </summary>
    private const int CacheCapacity = 128;

    /// <summary>
    /// Minimum gap between two failure log lines. Same reasoning as DragDropTarget's
    /// ReadFailureLogInterval: a shelf full of cards from an unreachable network share would
    /// otherwise write one line per card per rebuild. Since the line cannot name the path anyway
    /// (see the class remarks), the suppressed lines would be near-identical to the one that got
    /// through.
    /// </summary>
    private static readonly TimeSpan FailureLogInterval = TimeSpan.FromSeconds(5);

    private static long _lastFailureLogTicks;

    // SIIGBF_* -- IShellItemImageFactory::GetImage flags.
    private const int SIIGBF_BIGGERSIZEOK = 0x01;
    private const int SIIGBF_ICONONLY = 0x04;
    private const int SIIGBF_THUMBNAILONLY = 0x08;

    /// <summary>Cache key for every directory: one folder icon serves them all.</summary>
    private const string DirectoryKey = "<dir>";

    /// <summary>Cache key for every extensionless file.</summary>
    private const string NoExtensionKey = "<noext>";

    /// <summary>
    /// Extensions whose icon is baked into the individual file rather than registered for the
    /// type -- an .exe carries its own icon resource, a .lnk points at an arbitrary other icon.
    /// Keying those by extension would show the first executable's icon on every executable, so
    /// they fall back to the per-path key thumbnails use.
    /// </summary>
    private static readonly HashSet<string> PerFileIconExtensions = new(StringComparer.Ordinal)
    {
        ".exe", ".lnk", ".url", ".ico", ".cur", ".ani", ".scr", ".dll", ".msc", ".cpl",
    };

    private static readonly LruImageCache IconCache = new(CacheCapacity);
    private static readonly LruImageCache ThumbnailCache = new(CacheCapacity);

    /// <summary>
    /// The shell's preview image for <paramref name="path"/>, at most
    /// <paramref name="px"/> logical pixels on its longest side, frozen and ready to bind.
    /// Returns null when the path is missing or no handler could produce anything.
    ///
    /// <para>Asks for SIIGBF_THUMBNAILONLY first so a file whose type has no thumbnail handler
    /// fails fast instead of silently returning its type icon blown up to 256px; only then does it
    /// retry with SIIGBF_ICONONLY, so the caller still gets *something* to draw. SIIGBF_BIGGERSIZEOK
    /// lets the shell hand back a cached tile that is larger than asked for rather than resampling
    /// it down -- the returned bitmap is tagged 96dpi, so a caller must constrain it in layout
    /// (MaxWidth/MaxHeight) instead of assuming it is exactly <paramref name="px"/> across.</para>
    ///
    /// <para>Cached by path + last-write time + size, so an edited file re-renders on its own.</para>
    /// </summary>
    public static ImageSource? GetThumbnail(string? path, int px)
    {
        if (!IsUsableRequest(path, px) || !PathExists(path!, out _))
        {
            return null;
        }

        var key = $"{px}|{LastWriteTicks(path!)}|{path}";
        if (ThumbnailCache.TryGet(key, out var cached))
        {
            return cached;
        }

        var image = Capture(path!, px, wantThumbnail: true);
        ThumbnailCache.Set(key, image);
        return image;
    }

    /// <summary>
    /// The shell's type icon for <paramref name="path"/> at <paramref name="px"/> logical pixels,
    /// frozen and ready to bind. Returns null when the path is missing or the shell refuses.
    ///
    /// <para>Cached by lowercase extension, so the whole shelf shares one .xlsx icon; directories
    /// share <see cref="DirectoryKey"/> and extensionless files share <see cref="NoExtensionKey"/>.
    /// That means a folder with a custom desktop.ini icon shows the generic folder icon -- the
    /// deliberate trade for not doing one COM round-trip per card. Types that carry their own
    /// icon per file (<see cref="PerFileIconExtensions"/>) are excluded from that sharing.</para>
    /// </summary>
    public static ImageSource? GetIcon(string? path, int px)
    {
        if (!IsUsableRequest(path, px) || !PathExists(path!, out var isDirectory))
        {
            return null;
        }

        var key = IconCacheKey(path!, isDirectory, px);
        if (IconCache.TryGet(key, out var cached))
        {
            return cached;
        }

        var image = Capture(path!, px, wantThumbnail: false);
        IconCache.Set(key, image);
        return image;
    }

    /// <summary>
    /// Drops every cached image. Exists for diagnostics and for the leak probe, which has to force
    /// a real COM round-trip per iteration to observe the HBITMAP lifetime; normal app code should
    /// never need it (the caches are self-invalidating on last-write time and bounded by
    /// <see cref="CacheCapacity"/>).
    /// </summary>
    public static void ClearCache()
    {
        IconCache.Clear();
        ThumbnailCache.Clear();
    }

    /// <summary>
    /// Rejects requests before any file system or COM work. The 2048px ceiling is a sanity bound,
    /// not a shell limit: the two real call sites ask for 256 and 32, so a larger number means a
    /// caller bug, and letting it through would hand the shell an arbitrary allocation size.
    /// </summary>
    private static bool IsUsableRequest(string? path, int px) =>
        px > 0 && px <= 2048 && !string.IsNullOrWhiteSpace(path);

    private static string IconCacheKey(string path, bool isDirectory, int px)
    {
        if (isDirectory)
        {
            return $"{px}|{DirectoryKey}";
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext.Length == 0)
        {
            return $"{px}|{NoExtensionKey}";
        }

        return PerFileIconExtensions.Contains(ext)
            ? $"{px}|{LastWriteTicks(path)}|{path}"
            : $"{px}|{ext}";
    }

    /// <summary>
    /// Existence check done in managed code first, so a path the user deleted (a very common case
    /// -- the shelf outlives the files dropped on it) costs a file system stat instead of a COM
    /// activation plus a failing shell parse plus a log line.
    /// </summary>
    private static bool PathExists(string path, out bool isDirectory)
    {
        isDirectory = false;

        try
        {
            if (Directory.Exists(path))
            {
                isDirectory = true;
                return true;
            }

            return File.Exists(path);
        }
        catch (Exception ex)
        {
            // Exists() swallows most errors already; a malformed path can still surface one.
            LogFailure("exists", ex);
            return false;
        }
    }

    private static long LastWriteTicks(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch (Exception ex)
        {
            // Key on 0 instead: the cache entry then never self-invalidates, which is strictly
            // better than not caching a path whose timestamp we cannot read.
            LogFailure("stat", ex);
            return 0;
        }
    }

    private static ImageSource? Capture(string path, int px, bool wantThumbnail)
    {
        var op = wantThumbnail ? "thumbnail" : "icon";
        IShellItemImageFactory? factory = null;

        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);
            if (factory is null)
            {
                LogFailure(op, null);
                return null;
            }

            var size = new SIZE { cx = px, cy = px };
            var hbitmap = IntPtr.Zero;

            var hr = factory.GetImage(
                size,
                wantThumbnail ? SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK : SIIGBF_ICONONLY,
                out hbitmap);

            if (wantThumbnail && (hr < 0 || hbitmap == IntPtr.Zero))
            {
                // A handler that fails after allocating is not something we can rule out; free
                // whatever it did hand back before asking again, or the retry leaks it.
                if (hbitmap != IntPtr.Zero)
                {
                    DeleteObject(hbitmap);
                    hbitmap = IntPtr.Zero;
                }

                hr = factory.GetImage(size, SIIGBF_ICONONLY, out hbitmap);
            }

            if (hr < 0 || hbitmap == IntPtr.Zero)
            {
                if (hbitmap != IntPtr.Zero)
                {
                    DeleteObject(hbitmap);
                }

                LogFailure(op, null);
                return null;
            }

            try
            {
                return ToImageSource(hbitmap);
            }
            finally
            {
                // The HBITMAP is ours the moment GetImage succeeds, and the conversion copies the
                // pixels out of it (verified by the probe: the returned bitmap still reads back
                // correct pixels after this delete). Nothing outlives this frame holding it.
                DeleteObject(hbitmap);
            }
        }
        catch (Exception ex)
        {
            LogFailure(op, ex);
            return null;
        }
        finally
        {
            if (factory is not null)
            {
                Marshal.ReleaseComObject(factory);
            }
        }
    }

    /// <summary>
    /// Wraps the shell's HBITMAP as a frozen WPF bitmap.
    ///
    /// <para><b>Alpha.</b> The obvious worry here is that CreateBitmapSourceFromHBitmap discards
    /// the alpha channel of a 32bpp bitmap -- which would put black corners around every icon on
    /// TNDrop's dark cards. Measured on Windows 11 / .NET 10 (scratchpad probe, 2026-08-21) that
    /// is not what happens: the shell returns a 32bpp DIB section and the helper produces a
    /// <c>Bgra32</c> source with the alpha channel intact and the rows the right way up.</para>
    ///
    /// <para>An earlier version of this method read the DIB bits by hand to "fix" alpha. That was
    /// wrong twice over, and the probe caught both: (1) GetObject reports <c>biHeight</c> as
    /// POSITIVE (nominally bottom-up) for these bitmaps even though the memory is stored top-down,
    /// so any flip driven off that sign turns the image upside down; (2) the shell's 32bpp output
    /// is STRAIGHT alpha, not premultiplied -- 102 of 112 partially transparent pixels in a real
    /// shell icon had a colour channel above their alpha -- so labelling the buffer Pbgra32 washed
    /// out every anti-aliased edge. Bgra32 from the stock helper is simply correct.</para>
    ///
    /// <para>Residual limitation: if some future thumbnail handler returns a non-DIB or sub-32bpp
    /// HBITMAP, the helper yields an opaque format and any transparency in it is lost. Nothing
    /// here detects that, and it is accepted for v1.1.</para>
    /// </summary>
    private static ImageSource ToImageSource(IntPtr hbitmap)
    {
        var source = Imaging.CreateBitmapSourceFromHBitmap(
            hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }

    /// <summary>
    /// One throttled Warn per failure. Deliberately records only the operation and the exception
    /// *type*: <see cref="Exception.Message"/> from a shell or IO error routinely embeds the full
    /// path, which this log must never contain.
    /// </summary>
    private static void LogFailure(string op, Exception? ex)
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        var lastTicks = Interlocked.Read(ref _lastFailureLogTicks);

        if (lastTicks != 0 && new TimeSpan(nowTicks - lastTicks) < FailureLogInterval)
        {
            return;
        }

        // Best-effort throttle, as in DragDropTarget: a race costs an extra line, never a lost one.
        Interlocked.Exchange(ref _lastFailureLogTicks, nowTicks);
        FileLogger.Instance?.Warn(Module,
            ex is null
                ? $"shell {op} extraction returned nothing"
                : $"shell {op} extraction failed ({ex.GetType().Name})");
    }

    /// <summary>
    /// Least-recently-used image cache. A null value is a cached *failure*: re-asking the shell
    /// for an image it already refused, once per card per rebuild, is exactly the stall this class
    /// exists to avoid. Thumbnail keys carry the file's last-write time, so a failure there clears
    /// itself as soon as the file changes; an icon failure is keyed by extension and so sticks for
    /// the session, which is the intent (a type with no icon handler will not grow one).
    /// </summary>
    private sealed class LruImageCache
    {
        private readonly Dictionary<string, ImageSource?> _map = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<string> _order = new();
        private readonly object _lock = new();
        private readonly int _capacity;

        public LruImageCache(int capacity) => _capacity = capacity;

        public bool TryGet(string key, out ImageSource? value)
        {
            lock (_lock)
            {
                if (!_map.TryGetValue(key, out value))
                {
                    return false;
                }

                _order.Remove(key);
                _order.AddFirst(key);
                return true;
            }
        }

        public void Set(string key, ImageSource? value)
        {
            lock (_lock)
            {
                if (_map.ContainsKey(key))
                {
                    _order.Remove(key);
                }

                _map[key] = value;
                _order.AddFirst(key);

                while (_order.Count > _capacity)
                {
                    var oldest = _order.Last!.Value;
                    _order.RemoveLast();
                    _map.Remove(oldest);
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        // PreserveSig so the THUMBNAILONLY -> ICONONLY retry is an HRESULT check rather than an
        // exception on the hot path: a file type with no thumbnail handler is normal, not an error.
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

}
