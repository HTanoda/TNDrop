using System;
using System.Collections.Generic;
using System.Collections.Specialized;
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

    // Single source of truth for the three format NAMES: PrivacyFormats (the enumerable other
    // code/tests inspect) and EvaluatePrivacy (the logic that decides on them) both read from
    // these same three constants, so the two can never drift apart.
    private const string ExcludeFormat = "ExcludeClipboardContentFromMonitorProcessing";
    private const string HistoryFormat = "CanIncludeInClipboardHistory";
    private const string ViewerIgnoreFormat = "Clipboard Viewer Ignore";

    public static readonly string[] PrivacyFormats = { ExcludeFormat, HistoryFormat, ViewerIgnoreFormat };

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
    /// <para><b>Deliberately checks each name via <see cref="WpfDataObject.GetDataPresent(string)"/>
    /// instead of scanning <see cref="WpfDataObject.GetFormats()"/></b>: verified experimentally
    /// (real clipboard, [StaFact]) that GetFormats() can OMIT a custom registered format that was
    /// appended to the clipboard by a second OpenClipboard/SetClipboardData call after the first
    /// app already established the OLE data object (exactly the pattern real privacy-flag-setting
    /// apps use: set the payload via one clipboard write, then reopen and stamp the marker format
    /// without EmptyClipboard) -- while GetDataPresent(name) for that SAME format correctly returns
    /// true. Scanning GetFormats() for these three names would have silently never matched any of
    /// them for such content, defeating both the old presence-based checks and the new value-based
    /// one. Checking each of the three known names directly sidesteps that gap entirely.</para>
    /// </summary>
    private static IEnumerable<string> PrivacyFormatsPresentOn(WpfDataObject data) =>
        PrivacyFormats.Where(data.GetDataPresent);

    private static CapturedClip? ReadOnce()
    {
        WpfDataObject? data = WpfClipboard.GetDataObject();
        if (data is null)
            return null;

        var evaluation = EvaluatePrivacy(PrivacyFormatsPresentOn(data), ReadHistoryFlagValue);
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
    /// Reads the DWORD payload of the CanIncludeInClipboardHistory clipboard format straight off
    /// the live Win32 clipboard: RegisterClipboardFormat to resolve the format id, OpenClipboard
    /// (retried with the same 5-attempts/50ms*2^n backoff as <see cref="Retry{T}"/> -- OpenClipboard
    /// fails, not throws, when another process holds the clipboard open, so it needs its own loop
    /// rather than reusing Retry's exception-catching one) + GetClipboardData + GlobalLock/GlobalSize
    /// (>= 4 bytes) to read the value, GlobalUnlock, CloseClipboard in finally.
    /// Returns null on ANY failure (format not registered, clipboard never opens, handle missing,
    /// payload too small) -- per <see cref="EvaluatePrivacy"/>, null means "do not exclude".
    /// </summary>
    internal static uint? ReadHistoryFlagValue()
    {
        var format = RegisterClipboardFormat(HistoryFormat);
        if (format == 0)
            return null;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    return ReadDwordHandle(GetClipboardData(format));
                }
                finally
                {
                    CloseClipboard();
                }
            }

            if (attempt == MaxAttempts - 1)
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
