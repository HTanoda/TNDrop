using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media.Imaging;
using TNDrop.Core;
using TNDrop.Services;

// UseWindowsForms is on (for the tray NotifyIcon), so DataFormats/IDataObject are ambiguous
// between the WPF and WinForms flavours. Drag-drop routed events hand us the WPF ones: pin them.
using DataFormats = System.Windows.DataFormats;
using IDataObject = System.Windows.IDataObject;

namespace TNDrop.Platform;

/// <summary>
/// Classifies an inbound OLE drag payload (Explorer, a browser, another app) into a
/// <see cref="CapturedClip"/> for the shelf's drag-IN (Task 13). The mirror image of
/// <see cref="DragDropSource"/>, which builds the payload for a drag OUT of the shelf.
///
/// <para>Pure apart from reading the <see cref="IDataObject"/> handed in -- no UI, no clipboard,
/// no pipeline call -- so it is the unit under test rather than ShelfWindow's event handlers.</para>
/// </summary>
public static class DragDropTarget
{
    private const string Module = "DragDropTarget";

    /// <summary>
    /// Minimum gap between two "failed to read a drag format" log lines. DragOver fires
    /// continuously (many times a second) for as long as the drag stays over the shelf, and
    /// <see cref="HasAcceptablePayload"/> re-reads the payload on every one of those calls -- a
    /// drag source whose OLE data object throws (a flaky/slow app, a network-backed file) would
    /// otherwise spam one Warn line per frame for the whole hover. This throttles it to roughly
    /// one line per drag session instead of trying to track "session" as an explicit concept.
    /// </summary>
    private static readonly TimeSpan ReadFailureLogInterval = TimeSpan.FromSeconds(2);

    private static long _lastReadFailureLogTicks;

    /// <summary>
    /// True when the payload carries TNDrop's own <see cref="DragDropSource.CardIdFormat"/> marker,
    /// i.e. this is a card the user dragged OUT of the shelf (whether or not it lands back on it).
    /// The drop handler must ignore these entirely: routing one back through the capture pipeline
    /// would re-add the very card the user just dragged, an add-on-every-drag loop.
    /// </summary>
    public static bool IsSelfDrag(IDataObject? data) =>
        data is not null && GetDataPresent(data, DragDropSource.CardIdFormat);

    /// <summary>
    /// True when <paramref name="data"/> is a genuine external drop the shelf can turn into a
    /// card: not self-drag, and carrying at least one of FileDrop / Bitmap / UnicodeText. Drives
    /// the DragEnter/DragOver accept-affordance and the DragDropEffects choice; does not itself
    /// guarantee <see cref="ClipFromDataObject"/> will return non-null (a UnicodeText format can
    /// still be present with a whitespace-only string).
    /// </summary>
    public static bool HasAcceptablePayload(IDataObject? data) =>
        data is not null
        && !IsSelfDrag(data)
        && (GetDataPresent(data, DataFormats.FileDrop)
            || GetDataPresent(data, DataFormats.Bitmap)
            || GetDataPresent(data, DataFormats.UnicodeText));

    /// <summary>
    /// Builds the <see cref="CapturedClip"/> a drop should become, or null when there is nothing
    /// usable (self-drag, no recognised format, or a format present but empty/blank).
    /// <para>Priority FileDrop &gt; Bitmap &gt; UnicodeText, per the drag-in spec -- deliberately
    /// different from <see cref="Platform.ClipboardIo.ReadCurrent"/>'s Files &gt; Text &gt; Image
    /// clipboard-read order, which exists to let Office's CF_UNICODETEXT win over the CF_DIB it
    /// piggybacks alongside a copied cell range. A drag payload has no such passenger format to
    /// prefer past.</para>
    /// <para>Text is classified the same way a clipboard capture is: <see cref="UrlDetector.IsUrl"/>
    /// decides Link vs. Text. <see cref="CapturePipeline.Process"/> does not reclassify -- that
    /// only happens in <see cref="ClipboardIo.ReadCurrent"/> today -- so the drop handler has to
    /// run the same check itself rather than relying on the pipeline to do it.</para>
    /// </summary>
    public static CapturedClip? ClipFromDataObject(IDataObject? data)
    {
        if (data is null || IsSelfDrag(data))
        {
            return null;
        }

        if (GetDataPresent(data, DataFormats.FileDrop)
            && GetData(data, DataFormats.FileDrop) is string[] paths
            && paths.Length > 0)
        {
            return new CapturedClip { Kind = ClipKind.Files, Files = paths };
        }

        if (GetDataPresent(data, DataFormats.Bitmap)
            && GetData(data, DataFormats.Bitmap) is BitmapSource image)
        {
            return new CapturedClip { Kind = ClipKind.Image, Image = FreezeIfNeeded(image) };
        }

        if (GetDataPresent(data, DataFormats.UnicodeText)
            && GetData(data, DataFormats.UnicodeText) is string text
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
    /// A dropped bitmap can be backed by the source app's own memory (an interop bitmap over a
    /// GDI/OLE section); freezing before <see cref="CapturePipeline.Process"/> re-encodes it is
    /// what makes that safe. Processing happens synchronously inside the Drop handler, so no
    /// cross-thread copy is needed here -- unlike <see cref="ClipboardIo"/>'s clipboard read,
    /// which freezes AND copies because the clipboard's backing memory can be reclaimed the
    /// moment clipboard ownership changes, arbitrarily later.
    /// </summary>
    private static BitmapSource FreezeIfNeeded(BitmapSource source)
    {
        if (source.CanFreeze && !source.IsFrozen)
        {
            source.Freeze();
        }

        return source;
    }

    /// <summary>
    /// <see cref="IDataObject.GetDataPresent(string)"/>, defended against a flaky drag source.
    /// A COM/OLE call into another process's data object can fail transiently (the same class of
    /// external error <see cref="ClipboardIo"/> retries for the clipboard) -- but unlike a
    /// clipboard read, a drag payload is not worth retrying: DragOver will simply ask again a few
    /// milliseconds later on its own. A throw here is treated the same as "format absent".
    /// </summary>
    private static bool GetDataPresent(IDataObject data, string format)
    {
        try
        {
            return data.GetDataPresent(format);
        }
        catch (ExternalException ex)
        {
            LogReadFailure(format, ex);
            return false;
        }
    }

    /// <summary><see cref="IDataObject.GetData(string)"/>, defended the same way as <see cref="GetDataPresent"/>.</summary>
    private static object? GetData(IDataObject data, string format)
    {
        try
        {
            return data.GetData(format);
        }
        catch (ExternalException ex)
        {
            LogReadFailure(format, ex);
            return null;
        }
    }

    private static void LogReadFailure(string format, Exception ex)
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        var lastTicks = Interlocked.Read(ref _lastReadFailureLogTicks);

        if (lastTicks != 0
            && new DateTime(nowTicks, DateTimeKind.Utc) - new DateTime(lastTicks, DateTimeKind.Utc) < ReadFailureLogInterval)
        {
            return;
        }

        // Best-effort throttle, not exact: a race here just means an occasional extra line, never
        // a missing one -- fine for a log line, not worth a lock.
        Interlocked.Exchange(ref _lastReadFailureLogTicks, nowTicks);
        FileLogger.Instance?.Warn(Module,
            $"reading drag payload format '{format}' failed, treating it as absent: {ex.Message}");
    }
}
