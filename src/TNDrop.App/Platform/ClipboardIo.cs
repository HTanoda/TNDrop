using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media.Imaging;
using TNDrop.Core;
using TNDrop.Services;
using WpfClipboard = System.Windows.Clipboard;
using WpfDataFormats = System.Windows.DataFormats;
using WpfDataObject = System.Windows.IDataObject;

namespace TNDrop.Platform;

/// <summary>
/// Clipboard read/write with the retry discipline the Windows clipboard demands:
/// the clipboard is a single global resource and any process can hold it open,
/// so every OLE call may fail transiently with a COM/external error.
/// Nothing here throws; failures are logged and degrade to "no data".
/// </summary>
public static class ClipboardIo
{
    private const string Module = "ClipboardIo";
    private const int MaxAttempts = 5;
    private const int BaseDelayMs = 50;

    // Budget for the RAW WIN32 fallback read only (ReadHistoryFlagValueFromWin32) -- deliberately
    // smaller than MaxAttempts. That fallback runs synchronously on the UI Dispatcher thread inside
    // ReadOnce(), which is itself retried up to MaxAttempts times by Retry<T> on ExternalException.
    // Nesting the file's usual 5-attempt/50ms*2^n inner loop inside that outer retry gave a
    // worst-case stall of roughly MaxAttempts * (a full 5-attempt backoff, ~750ms of sleeps) =~
    // 4.5s of a blocked UI thread if the clipboard stayed transiently uncooperative the whole time.
    // Capping this inner budget to 2 attempts (one 50ms sleep) bounds the worst case to roughly
    // MaxAttempts * 50ms =~ 250ms even if every outer retry hits the fallback. The cost of giving
    // up early is exactly one screenshot read as "unreadable" (fail-open: NOT excluded, see
    // EvaluatePrivacy) rather than as its true value -- and this fallback is now rare in practice,
    // since ReadHistoryFlagValue's fast path (below) reads the value off the already-captured
    // IDataObject with no live clipboard access at all in the common case.
    private const int HistoryFlagMaxAttempts = 2;

    // Single source of truth for the three format NAMES: PrivacyFormats (the enumerable other
    // code/tests inspect) and EvaluatePrivacy (the logic that decides on them) both read from
    // these same three constants, so the two can never drift apart.
    private const string ExcludeFormat = "ExcludeClipboardContentFromMonitorProcessing";
    private const string HistoryFormat = "CanIncludeInClipboardHistory";
    private const string ViewerIgnoreFormat = "Clipboard Viewer Ignore";

    public static readonly IReadOnlyList<string> PrivacyFormats = new[] { ExcludeFormat, HistoryFormat, ViewerIgnoreFormat };

    /// <summary>Result of <see cref="EvaluatePrivacy"/>: whether to exclude, and (for logging) which format matched.</summary>
    public readonly record struct PrivacyEvaluation(bool Excluded, string? MatchedFormat);

    /// <summary>
    /// True if any known privacy/exclusion clipboard format is present.
    /// This overload has no way to read <see cref="HistoryFormat"/>'s DWORD payload, so it falls
    /// back to the old presence-based rule for all three formats (see <see cref="EvaluatePrivacy"/>
    /// remarks for why that is wrong for HistoryFormat specifically). Kept only for callers that
    /// cannot supply a live clipboard value reader (see <see cref="DragDropTarget"/> notes) and for
    /// pure presence-based unit tests; <see cref="ReadCurrent"/> itself no longer uses this overload.
    /// </summary>
    public static bool HasPrivacyFlag(IEnumerable<string> formats) => EvaluatePrivacy(formats, null).Excluded;

    /// <summary>
    /// Evaluates the known privacy/exclusion clipboard formats against <paramref name="formats"/>.
    /// <list type="bullet">
    /// <item><see cref="ExcludeFormat"/> and <see cref="ViewerIgnoreFormat"/> exclude on mere
    /// PRESENCE: no known producer attaches either format with a "false" payload, so presence
    /// alone is a safe signal (unchanged from v1).</item>
    /// <item><see cref="HistoryFormat"/> is different. Windows' own Snipping Tool / Win+Shift+S
    /// attaches this format to EVERY screenshot with a DWORD payload of 1 ("yes, allow me in
    /// clipboard history") -- so excluding on presence alone silently ate every screenshot (the
    /// v1 bug this method exists to fix). Per the documented clipboard-history contract, 0 means
    /// "exclude me", nonzero means "allow me". <paramref name="readHistoryFlagValue"/> reads that
    /// payload lazily -- it is only invoked when the format is actually present, and its result
    /// (or failure) alone decides the outcome: value 0 excludes, any other value or a failed
    /// read (exception or null) does NOT exclude, per the brief's "when in doubt, don't drop the
    /// user's screenshot" stance. Pass null when no live clipboard is available (falls back to the
    /// old presence-based rule) -- see <see cref="HasPrivacyFlag(IEnumerable{string})"/>.</item>
    /// </list>
    /// </summary>
    public static PrivacyEvaluation EvaluatePrivacy(IEnumerable<string>? formats, Func<uint?>? readHistoryFlagValue)
    {
        if (formats is null)
            return new PrivacyEvaluation(false, null);

        string? historyFormat = null;

        // Win32 registered clipboard format names compare case-insensitively.
        foreach (var f in formats)
        {
            if (f is null)
                continue;

            if (string.Equals(f, ExcludeFormat, StringComparison.OrdinalIgnoreCase)
                || string.Equals(f, ViewerIgnoreFormat, StringComparison.OrdinalIgnoreCase))
            {
                return new PrivacyEvaluation(true, f);
            }

            if (historyFormat is null && string.Equals(f, HistoryFormat, StringComparison.OrdinalIgnoreCase))
                historyFormat = f;
        }

        if (historyFormat is null)
            return new PrivacyEvaluation(false, null);

        if (readHistoryFlagValue is null)
            return new PrivacyEvaluation(true, historyFormat); // conservative fallback, see remarks above

        uint? value;
        try
        {
            value = readHistoryFlagValue();
        }
        catch
        {
            value = null; // unreadable -> do not exclude
        }

        return value == 0
            ? new PrivacyEvaluation(true, historyFormat)
            : new PrivacyEvaluation(false, null);
    }

    /// <summary>
    /// Reads the current clipboard content. Returns null when there is nothing usable,
    /// when a privacy format is present, or when every retry failed.
    /// Must be called on an STA thread (the WPF UI thread).
    /// </summary>
    public static CapturedClip? ReadCurrent(FileLogger? log)
        => Retry(Module, "ReadCurrent", ReadOnce, log);

    /// <summary>
    /// Which of <see cref="PrivacyFormats"/> are actually on <paramref name="data"/> right now.
    /// <para><b>Unions two independent detection channels</b> rather than relying on either alone,
    /// because each has a gap the other covers:</para>
    /// <list type="bullet">
    /// <item><see cref="WpfDataObject.GetDataPresent(string)"/> per name -- verified experimentally
    /// (real clipboard, [StaFact]) that <see cref="WpfDataObject.GetFormats()"/> can OMIT a custom
    /// registered format that was appended to the clipboard by a second OpenClipboard/
    /// SetClipboardData call after the first app already established the OLE data object (exactly
    /// the pattern real privacy-flag-setting apps use: set the payload via one clipboard write, then
    /// reopen and stamp the marker format without EmptyClipboard) -- while GetDataPresent(name) for
    /// that SAME format correctly returns true. Scanning GetFormats() ALONE for these three names
    /// would have silently never matched any of them for such content.</item>
    /// <item><see cref="WpfDataObject.GetFormats()"/> intersected with the three known names -- the
    /// reverse gap: GetDataPresent probes under WPF's own fixed set of allowed TYMEDs, so a foreign
    /// data object that advertises one of these formats via EnumFormatEtc under a TYMED outside that
    /// set answers GetFormats() "yes" but GetDataPresent(name) "no". Relying on GetDataPresent ALONE
    /// (the v1.2-task-G-first-pass approach, which section-1's finding above superseded) would then
    /// silently un-exclude content v1 correctly excluded on presence.</item>
    /// </list>
    /// <para>These formats are fail-closed by design (presence -- or, for HistoryFormat, a readable
    /// zero value -- must never go undetected just because one particular API surface missed it), so
    /// the union is deliberate: either channel seeing a format is enough to report it as present.</para>
    /// </summary>
    private static IEnumerable<string> PrivacyFormatsPresentOn(WpfDataObject data)
    {
        var viaPresence = PrivacyFormats.Where(data.GetDataPresent);
        var viaEnumeration = SafeGetFormats(data)
            .Where(f => f is not null && PrivacyFormats.Contains(f, StringComparer.OrdinalIgnoreCase));

        return viaPresence.Union(viaEnumeration, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <see cref="WpfDataObject.GetFormats()"/> can return null (undocumented, but seen from a
    /// failed/empty COM data object); never let that null propagate into a LINQ pipeline. Not
    /// wrapped in try/catch: GetFormats() throwing is exactly the class of transient COM failure
    /// <see cref="Retry{T}"/> already retries the whole <see cref="ReadOnce"/> call for.
    /// </summary>
    private static string[] SafeGetFormats(WpfDataObject data) => data.GetFormats() ?? Array.Empty<string>();

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private static CapturedClip? ReadOnce()
    {
        // Captured BEFORE GetDataObject() so the raw Win32 fallback (see ReadHistoryFlagValue) can
        // detect whether the clipboard changed underneath this read -- see its remarks.
        var sequenceAtSnapshot = GetClipboardSequenceNumber();

        WpfDataObject? data = WpfClipboard.GetDataObject();
        if (data is null)
            return null;

        var evaluation = EvaluatePrivacy(
            PrivacyFormatsPresentOn(data),
            () => ReadHistoryFlagValue(data, sequenceAtSnapshot));
        if (evaluation.Excluded)
        {
            // Diagnostic per the brief: format NAME only -- never content, size, or paths.
            FileLogger.Instance?.Info(Module, $"capture skipped: privacy format present ({evaluation.MatchedFormat})");
            return null;
        }

        // Priority: Files > Text > Image.
        // Text beats Image because Office puts CF_DIB on the clipboard alongside CF_UNICODETEXT
        // for a copied cell range, and the user means the text. Sources that really are images
        // (screenshot tools, browser "copy image") carry no plain text, so they still land as Image.
        if (data.GetDataPresent(WpfDataFormats.FileDrop)
            && data.GetData(WpfDataFormats.FileDrop) is string[] paths
            && paths.Length > 0)
        {
            return new CapturedClip { Kind = ClipKind.Files, Files = paths };
        }

        if (data.GetDataPresent(WpfDataFormats.UnicodeText)
            && data.GetData(WpfDataFormats.UnicodeText) is string text
            && !string.IsNullOrWhiteSpace(text))
        {
            return new CapturedClip
            {
                Kind = UrlDetector.IsUrl(text) ? ClipKind.Link : ClipKind.Text,
                Text = text,
            };
        }

        // Deliberate fall-through: blank/whitespace text does not veto an accompanying bitmap.
        // The bitmap is pulled from the SAME IDataObject the privacy check and the branches above
        // used. Calling Clipboard.GetImage() here would re-resolve the clipboard and could mix two
        // generations -- privacy-checking one clipboard and returning the contents of the next.
        if (data.GetDataPresent(WpfDataFormats.Bitmap)
            && data.GetData(WpfDataFormats.Bitmap) is BitmapSource image)
        {
            return new CapturedClip { Kind = ClipKind.Image, Image = FreezeForCrossThread(image) };
        }

        return null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr hMem);

    /// <summary>
    /// Reads the DWORD payload of the CanIncludeInClipboardHistory clipboard format.
    /// <para><b>Fast path (the common case, no live clipboard access at all):</b> ask the SAME
    /// <paramref name="data"/> object <see cref="ReadOnce"/> already captured for its data under
    /// this format name. Verified experimentally (real clipboard, [StaFact]): for a custom
    /// HGLOBAL-backed format WPF has no built-in type mapping for, <c>IDataObject.GetData</c>
    /// returns a <see cref="MemoryStream"/> over the raw bytes -- reading that costs nothing further
    /// and, critically, comes from the EXACT SAME clipboard generation as the privacy check and the
    /// Files/Text/Image branches in <see cref="ReadOnce"/>, avoiding the single-generation
    /// principle violation a second live clipboard read would otherwise introduce (decision and
    /// content must never be read from two different clipboard "generations" -- see the class
    /// remarks on <see cref="ReadOnce"/>'s Bitmap branch for the established precedent).</para>
    /// <para><b>Fallback (rare):</b> if the format did not arrive as a MemoryStream on
    /// <paramref name="data"/> (e.g. this call came from the GetFormats()-only detection channel in
    /// <see cref="PrivacyFormatsPresentOn"/>, where GetDataPresent -- and therefore GetData -- says
    /// no), reopen the LIVE clipboard via <see cref="ReadHistoryFlagValueFromWin32"/>. That
    /// necessarily reintroduces a TOCTOU window against <paramref name="data"/>'s generation, so it
    /// is guarded by <paramref name="sequenceAtSnapshot"/> (the sequence number captured
    /// immediately before <c>WpfClipboard.GetDataObject()</c> produced <paramref name="data"/>): a
    /// mismatch after the raw read means the clipboard changed in between, and the value is
    /// discarded (fail-open, i.e. treated the same as an unreadable value).</para>
    /// </summary>
    private static uint? ReadHistoryFlagValue(WpfDataObject data, uint sequenceAtSnapshot)
    {
        try
        {
            if (data.GetData(HistoryFormat) is MemoryStream stream)
            {
                var bytes = stream.ToArray();
                if (bytes.Length >= sizeof(uint))
                    return BitConverter.ToUInt32(bytes, 0);

                return null; // payload present but too small -- unreadable, do not exclude
            }
        }
        catch
        {
            // Fall through to the raw fallback below.
        }

        return ReadHistoryFlagValueFromWin32(sequenceAtSnapshot);
    }

    /// <summary>
    /// Raw Win32 fallback for <see cref="ReadHistoryFlagValue"/>: RegisterClipboardFormat to
    /// resolve the format id, OpenClipboard (retried up to <see cref="HistoryFlagMaxAttempts"/>
    /// times with the file's usual 50ms*2^n backoff -- OpenClipboard fails, not throws, when
    /// another process holds the clipboard open, so it needs its own loop rather than reusing
    /// <see cref="Retry{T}"/>'s exception-catching one) + GetClipboardData + GlobalLock/GlobalSize
    /// (>= 4 bytes) to read the value, GlobalUnlock, CloseClipboard in finally.
    /// <para>TOCTOU guard: re-reads <see cref="GetClipboardSequenceNumber"/> immediately after the
    /// raw read and compares it to <paramref name="sequenceAtSnapshot"/> (captured before the
    /// caller's <c>GetDataObject()</c>); a mismatch discards the value -- the clipboard changed
    /// underneath this read, so what was just read may not belong to the content the rest of
    /// <see cref="ReadOnce"/> is about to process.</para>
    /// Returns null on ANY failure (format not registered, clipboard never opens, handle missing,
    /// payload too small, sequence number mismatch) -- per <see cref="EvaluatePrivacy"/>, null means
    /// "do not exclude".
    /// </summary>
    private static uint? ReadHistoryFlagValueFromWin32(uint sequenceAtSnapshot)
    {
        var format = RegisterClipboardFormat(HistoryFormat);
        if (format == 0)
            return null;

        for (var attempt = 0; attempt < HistoryFlagMaxAttempts; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    var value = ReadDwordHandle(GetClipboardData(format));
                    return GetClipboardSequenceNumber() == sequenceAtSnapshot ? value : null;
                }
                finally
                {
                    CloseClipboard();
                }
            }

            if (attempt == HistoryFlagMaxAttempts - 1)
            {
                FileLogger.Instance?.Warn(Module,
                    $"OpenClipboard failed while reading {HistoryFormat} value (Win32 error {Marshal.GetLastWin32Error()})");
                return null;
            }

            Thread.Sleep(BaseDelayMs * (1 << attempt));
        }

        return null;
    }

    private static uint? ReadDwordHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
            return null;

        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero)
            return null;

        try
        {
            if (GlobalSize(handle).ToUInt64() < sizeof(uint))
                return null;

            return unchecked((uint)Marshal.ReadInt32(ptr));
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    /// <summary>
    /// The bitmap the clipboard hands back is an interop bitmap over a clipboard-owned memory
    /// section. Copy it into managed memory and freeze it so it survives the clipboard changing
    /// and can be handed to other threads.
    /// </summary>
    private static BitmapSource FreezeForCrossThread(BitmapSource source)
    {
        try
        {
            var copy = new WriteableBitmap(source);
            copy.Freeze();
            return copy;
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Warn(Module, $"bitmap copy failed, freezing source instead: {ex.Message}");
            if (source.CanFreeze && !source.IsFrozen)
                source.Freeze();
            return source;
        }
    }

    public static void SetText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        Retry<object?>(Module, "SetText", () =>
        {
            WpfClipboard.SetText(text, System.Windows.TextDataFormat.UnicodeText);
            return null;
        }, FileLogger.Instance);
    }

    public static void SetFiles(string[] paths)
    {
        if (paths is null)
            return;

        var list = new StringCollection();
        foreach (var p in paths)
        {
            if (!string.IsNullOrWhiteSpace(p))
                list.Add(p);
        }

        if (list.Count == 0)
            return;

        // SetFileDropList produces CF_HDROP, which Explorer and Office accept for paste.
        Retry<object?>(Module, "SetFiles", () =>
        {
            WpfClipboard.SetFileDropList(list);
            return null;
        }, FileLogger.Instance);
    }

    public static void SetImage(BitmapSource img)
    {
        if (img is null)
            return;

        Retry<object?>(Module, "SetImage", () =>
        {
            WpfClipboard.SetImage(img);
            return null;
        }, FileLogger.Instance);
    }

    /// <summary>
    /// Runs <paramref name="action"/> up to 5 times, backing off 50ms * 2^attempt between
    /// transient clipboard failures (COMException derives from ExternalException).
    /// Any other exception is logged once and gives up: never rethrown.
    /// </summary>
    private static T? Retry<T>(string module, string operation, Func<T?> action, FileLogger? log)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                return action();
            }
            catch (ExternalException ex)
            {
                if (attempt == MaxAttempts - 1)
                {
                    log?.Error(module, $"{operation} failed after {MaxAttempts} attempts", ex);
                    return default;
                }

                Thread.Sleep(BaseDelayMs * (1 << attempt));
            }
            catch (Exception ex)
            {
                log?.Error(module, $"{operation} failed", ex);
                return default;
            }
        }

        return default;
    }
}
