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

    public static readonly string[] PrivacyFormats =
    {
        "ExcludeClipboardContentFromMonitorProcessing",
        "CanIncludeInClipboardHistory",
        "Clipboard Viewer Ignore",
    };

    /// <summary>
    /// True if any known privacy/exclusion clipboard format is present.
    /// Note: CanIncludeInClipboardHistory technically means "excluded when the value is 0",
    /// but v1 excludes on the mere presence of the format (safe side: its presence strongly
    /// suggests a password manager or similar privacy-sensitive source).
    /// </summary>
    public static bool HasPrivacyFlag(IEnumerable<string> formats)
    {
        if (formats is null)
            return false;

        // Win32 registered clipboard format names compare case-insensitively.
        return formats.Any(f => f is not null
            && PrivacyFormats.Contains(f, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reads the current clipboard content. Returns null when there is nothing usable,
    /// when a privacy format is present, or when every retry failed.
    /// Must be called on an STA thread (the WPF UI thread).
    /// </summary>
    public static CapturedClip? ReadCurrent(FileLogger? log)
        => Retry(Module, "ReadCurrent", ReadOnce, log);

    private static CapturedClip? ReadOnce()
    {
        WpfDataObject? data = WpfClipboard.GetDataObject();
        if (data is null)
            return null;

        if (HasPrivacyFlag(data.GetFormats()))
            return null;

        // Priority: Files > Image > Text
        if (data.GetDataPresent(WpfDataFormats.FileDrop)
            && data.GetData(WpfDataFormats.FileDrop) is string[] paths
            && paths.Length > 0)
        {
            return new CapturedClip { Kind = ClipKind.Files, Files = paths };
        }

        if (data.GetDataPresent(WpfDataFormats.Bitmap))
        {
            var image = WpfClipboard.GetImage();
            if (image is not null)
                return new CapturedClip { Kind = ClipKind.Image, Image = FreezeForCrossThread(image) };
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

        return null;
    }

    /// <summary>
    /// The bitmap handed back by Clipboard.GetImage() is an interop bitmap over a
    /// clipboard-owned memory section. Copy it into managed memory and freeze it so it
    /// survives the clipboard changing and can be handed to other threads.
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
