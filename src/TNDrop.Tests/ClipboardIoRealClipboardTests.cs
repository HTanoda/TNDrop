using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TNDrop.Core;
using TNDrop.Platform;
using Xunit.Abstractions;
using WpfClipboard = System.Windows.Clipboard;
using WpfDataObject = System.Windows.DataObject;
using WpfIDataObject = System.Windows.IDataObject;

namespace TNDrop.Tests;

/// <summary>
/// Exercises <see cref="ClipboardIo.ReadCurrent"/> against the REAL Win32 clipboard. This is the
/// only way to prove the CanIncludeInClipboardHistory value-reading path (RegisterClipboardFormat +
/// OpenClipboard + GetClipboardData + GlobalLock/GlobalSize, and the MemoryStream fast path in
/// <c>ClipboardIo.ReadHistoryFlagValue</c>) actually works end to end -- it is raw Win32, not
/// something a purely in-memory WPF <see cref="WpfDataObject"/> can fake, since
/// <c>DataObject.SetData(name, intValue)</c> boxes the int as a serialized .NET object rather than
/// the raw little-endian DWORD Windows/Snipping Tool actually write.
///
/// <para><b>Approach</b>: set the bitmap through WPF's <see cref="WpfClipboard.SetImage"/> (produces
/// CF_DIB/CF_BITMAP the normal way), then reopen the clipboard WITHOUT calling EmptyClipboard and
/// append the CanIncludeInClipboardHistory format via raw <c>SetClipboardData</c>. Per the Win32
/// clipboard docs, EmptyClipboard is only required to take clipboard OWNERSHIP (relevant for
/// delayed rendering / WM_DESTROYCLIPBOARD); an application that has merely opened the clipboard can
/// still add an additional format without emptying it, which leaves the bitmap already on the
/// clipboard untouched. This is the same technique real privacy-flag-aware apps (password managers)
/// use to mark clipboard content they just wrote.</para>
///
/// <para><b>Self-cleaning</b>: every test snapshot-copies the clipboard's current content into a
/// FRESH <see cref="WpfDataObject"/> up front (see <see cref="SnapshotClipboard"/>) and restores it
/// in a finally block -- copying into a fresh object rather than holding onto the live
/// <see cref="WpfIDataObject"/> reference means the restore does not depend on the original OLE data
/// object (or its owning app) still being alive/valid by the time the test finishes. This runs
/// against the real developer machine clipboard (there is no clipboard sandbox / virtual desktop
/// isolation here), and the dev machine is in active use per the task brief.</para>
///
/// <para><b>Guarded against the running app</b>: if TNDrop.exe (this repo's own app, process name
/// "TNDrop") is running, every test in this class no-ops instead of touching the clipboard. Reason:
/// TNDrop's own <c>ClipboardMonitor</c> listens for WM_CLIPBOARDUPDATE globally and would react to
/// these tests' synthetic clipboard writes exactly like a real user copy, persisting them into the
/// REAL <c>%APPDATA%\TNDrop</c> item store -- which the project's global constraints explicitly
/// forbid touching from a probe/test run. xunit 2.9.3 (this project's pinned version, see
/// TNDrop.Tests.csproj) has no built-in dynamic Assert.Skip/SkipException (that arrived in xunit v3,
/// and adding a package such as Xunit.SkippableFact is against this repo's "no new NuGet" policy),
/// so the guard is a plain runtime check + early return + a loud <see cref="ITestOutputHelper"/>
/// line, rather than a framework-recognized "Skipped" status.</para>
/// </summary>
public class ClipboardIoRealClipboardTests
{
    private const string HistoryFormatName = "CanIncludeInClipboardHistory";
    private const string ExcludeFormatName = "ExcludeClipboardContentFromMonitorProcessing";
    private const uint GMEM_MOVEABLE = 0x0002;

    private readonly ITestOutputHelper _output;

    public ClipboardIoRealClipboardTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    private static BitmapSource TinyBitmap() =>
        BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgr32, null, new byte[2 * 2 * 4], 8);

    /// <summary>
    /// True when TNDrop.exe is running anywhere on this machine. See class remarks: every test
    /// checks this first and no-ops rather than risk polluting the real %APPDATA%\TNDrop store.
    /// </summary>
    private static bool IsTNDropAppRunning() => Process.GetProcessesByName("TNDrop").Length > 0;

    /// <summary>
    /// Logs the skip and returns true when the guarded test should no-op. Call as the very first
    /// line of every <see cref="StaFactAttribute"/> test method in this class, before any clipboard
    /// mutation.
    /// </summary>
    private bool SkipIfTNDropRunning([System.Runtime.CompilerServices.CallerMemberName] string testName = "")
    {
        if (!IsTNDropAppRunning())
            return false;

        _output.WriteLine(
            $"SKIPPED {testName}: TNDrop.exe is running -- refusing to touch the real clipboard " +
            "to avoid the running app's ClipboardMonitor persisting this test's synthetic content " +
            "into the real %APPDATA%\\TNDrop store.");
        return true;
    }

    /// <summary>
    /// Copies the clipboard's CURRENT content into a fresh, independent <see cref="WpfDataObject"/>
    /// up front, rather than holding onto the live <see cref="WpfIDataObject"/> from
    /// <see cref="WpfClipboard.GetDataObject"/> -- that live reference can become stale (e.g. if the
    /// owning app exits or itself changes the clipboard again) by the time a test's finally block
    /// tries to restore it. Best-effort per format: a format that fails to copy is simply omitted
    /// from the snapshot rather than aborting the whole snapshot.
    /// </summary>
    private static WpfDataObject? SnapshotClipboard()
    {
        var live = WpfClipboard.GetDataObject();
        if (live is null)
            return null;

        var snapshot = new WpfDataObject();
        var any = false;

        foreach (var format in live.GetFormats() ?? Array.Empty<string>())
        {
            try
            {
                var value = live.GetData(format);
                if (value is null)
                    continue;

                snapshot.SetData(format, value);
                any = true;
            }
            catch
            {
                // Best-effort: skip formats that fail to copy rather than losing the whole snapshot.
            }
        }

        return any ? snapshot : null;
    }

    /// <summary>
    /// Puts a bitmap on the clipboard via WPF, then appends CanIncludeInClipboardHistory with the
    /// given raw DWORD <paramref name="value"/> via Win32 (no EmptyClipboard -- see class remarks).
    /// </summary>
    private static void SetImageWithHistoryFlag(uint value)
    {
        WpfClipboard.SetImage(TinyBitmap());
        AppendRawDword(HistoryFormatName, value);
    }

    /// <summary>
    /// Puts a bitmap on the clipboard via WPF, then appends CanIncludeInClipboardHistory with a
    /// payload SHORTER than a DWORD (2 bytes) -- exercises ClipboardIo's "payload too small to be a
    /// DWORD" fail-open branch (GlobalSize/MemoryStream length &lt; 4) end to end.
    /// </summary>
    private static void SetImageWithTooSmallHistoryFlagPayload()
    {
        WpfClipboard.SetImage(TinyBitmap());

        var format = RegisterClipboardFormat(HistoryFormatName);
        Assert.NotEqual(0u, format);

        var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)2u);
        Assert.NotEqual(IntPtr.Zero, hMem);

        var ptr = GlobalLock(hMem);
        Assert.NotEqual(IntPtr.Zero, ptr);
        Marshal.WriteInt16(ptr, 0); // 2 bytes -- shorter than the DWORD the reader expects
        GlobalUnlock(hMem);

        Assert.True(OpenClipboard(IntPtr.Zero), "OpenClipboard failed while attaching CanIncludeInClipboardHistory");
        try
        {
            var result = SetClipboardData(format, hMem);
            Assert.NotEqual(IntPtr.Zero, result);
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// Puts a bitmap on the clipboard via WPF, then appends the (presence-based) Exclude marker
    /// format via Win32 -- carries a 1-byte dummy payload (not IntPtr.Zero) purely so the
    /// SetClipboardData return value is distinguishable from failure: a NULL hMem and a failed call
    /// both return IntPtr.Zero, so passing IntPtr.Zero would make the "did this actually work"
    /// assertion meaningless. The marker's own semantics remain presence-only; the payload's content
    /// is never read by ClipboardIo for this format.
    /// </summary>
    private static void SetImageWithExcludeFormat()
    {
        WpfClipboard.SetImage(TinyBitmap());
        AppendRawDword(ExcludeFormatName, 0);
    }

    /// <summary>Appends <paramref name="formatName"/> to the clipboard with a raw 4-byte DWORD payload, without EmptyClipboard.</summary>
    private static void AppendRawDword(string formatName, uint value)
    {
        var format = RegisterClipboardFormat(formatName);
        Assert.NotEqual(0u, format);

        var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)4u);
        Assert.NotEqual(IntPtr.Zero, hMem);

        var ptr = GlobalLock(hMem);
        Assert.NotEqual(IntPtr.Zero, ptr);
        Marshal.WriteInt32(ptr, unchecked((int)value));
        GlobalUnlock(hMem);

        Assert.True(OpenClipboard(IntPtr.Zero), $"OpenClipboard failed while attaching {formatName}");
        try
        {
            var result = SetClipboardData(format, hMem);
            Assert.NotEqual(IntPtr.Zero, result); // nonzero hMem, so success/failure ARE distinguishable here
        }
        finally
        {
            CloseClipboard();
        }
    }

    [StaFact]
    public void History_flag_value_one_does_not_exclude_the_screenshot()
    {
        if (SkipIfTNDropRunning())
            return;

        var saved = SnapshotClipboard();
        try
        {
            SetImageWithHistoryFlag(1);

            var clip = ClipboardIo.ReadCurrent(null);

            Assert.NotNull(clip);
            Assert.Equal(ClipKind.Image, clip!.Kind);
        }
        finally
        {
            Restore(saved);
        }
    }

    [StaFact]
    public void History_flag_value_zero_excludes_the_screenshot()
    {
        if (SkipIfTNDropRunning())
            return;

        var saved = SnapshotClipboard();
        try
        {
            SetImageWithHistoryFlag(0);

            var clip = ClipboardIo.ReadCurrent(null);

            Assert.Null(clip);
        }
        finally
        {
            Restore(saved);
        }
    }

    [StaFact]
    public void History_flag_payload_too_small_does_not_exclude_the_screenshot()
    {
        if (SkipIfTNDropRunning())
            return;

        var saved = SnapshotClipboard();
        try
        {
            SetImageWithTooSmallHistoryFlagPayload();

            var clip = ClipboardIo.ReadCurrent(null);

            // Unreadable (payload too short to be a DWORD) -> fail-open -> NOT excluded.
            Assert.NotNull(clip);
            Assert.Equal(ClipKind.Image, clip!.Kind);
        }
        finally
        {
            Restore(saved);
        }
    }

    [StaFact]
    public void Exclude_format_present_excludes_regardless_of_history_flag()
    {
        if (SkipIfTNDropRunning())
            return;

        var saved = SnapshotClipboard();
        try
        {
            SetImageWithExcludeFormat();

            var clip = ClipboardIo.ReadCurrent(null);

            Assert.Null(clip);
        }
        finally
        {
            Restore(saved);
        }
    }

    /// <summary>
    /// Restores whatever was on the clipboard before the test ran (a fresh snapshot copy -- see
    /// <see cref="SnapshotClipboard"/>). Best-effort: a restore failure must never mask the test's
    /// real assertion result, and there is nothing further we can do if the clipboard refuses the
    /// write-back.
    /// </summary>
    private static void Restore(WpfDataObject? saved)
    {
        try
        {
            if (saved is not null)
                WpfClipboard.SetDataObject(saved, true);
            else
                WpfClipboard.Clear();
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}
