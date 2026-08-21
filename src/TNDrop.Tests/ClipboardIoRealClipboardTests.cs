using System;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TNDrop.Core;
using TNDrop.Platform;
using WpfClipboard = System.Windows.Clipboard;
using WpfIDataObject = System.Windows.IDataObject;

namespace TNDrop.Tests;

/// <summary>
/// Exercises <see cref="ClipboardIo.ReadCurrent"/> against the REAL Win32 clipboard. This is the
/// only way to prove the CanIncludeInClipboardHistory value-reading path (RegisterClipboardFormat +
/// OpenClipboard + GetClipboardData + GlobalLock/GlobalSize) actually works end to end -- it is raw
/// Win32, not something a purely in-memory WPF <see cref="System.Windows.DataObject"/> can fake,
/// since <c>DataObject.SetData(name, intValue)</c> boxes the int as a serialized .NET object rather
/// than the raw little-endian DWORD Windows/Snipping Tool actually write.
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
/// <para><b>Self-cleaning</b>: every test saves the clipboard's current <see cref="WpfIDataObject"/>
/// up front and restores it in a finally block -- this runs against the real developer machine
/// clipboard (there is no clipboard sandbox / virtual desktop isolation here), and the dev machine
/// is in active use per the task brief.</para>
/// </summary>
public class ClipboardIoRealClipboardTests
{
    private const string HistoryFormatName = "CanIncludeInClipboardHistory";
    private const string ExcludeFormatName = "ExcludeClipboardContentFromMonitorProcessing";
    private const uint GMEM_MOVEABLE = 0x0002;

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
    /// Puts a bitmap on the clipboard via WPF, then appends CanIncludeInClipboardHistory with the
    /// given raw DWORD <paramref name="value"/> via Win32 (no EmptyClipboard -- see class remarks).
    /// </summary>
    private static void SetImageWithHistoryFlag(uint value)
    {
        WpfClipboard.SetImage(TinyBitmap());

        var format = RegisterClipboardFormat(HistoryFormatName);
        Assert.NotEqual(0u, format);

        var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)4u);
        Assert.NotEqual(IntPtr.Zero, hMem);

        var ptr = GlobalLock(hMem);
        Assert.NotEqual(IntPtr.Zero, ptr);
        Marshal.WriteInt32(ptr, unchecked((int)value));
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
    /// format with a NULL payload handle -- the documented way to attach a marker with no data.
    /// </summary>
    private static void SetImageWithExcludeFormat()
    {
        WpfClipboard.SetImage(TinyBitmap());

        var format = RegisterClipboardFormat(ExcludeFormatName);
        Assert.NotEqual(0u, format);

        Assert.True(OpenClipboard(IntPtr.Zero), "OpenClipboard failed while attaching ExcludeClipboardContentFromMonitorProcessing");
        try
        {
            SetClipboardData(format, IntPtr.Zero);
        }
        finally
        {
            CloseClipboard();
        }
    }

    [StaFact]
    public void History_flag_value_one_does_not_exclude_the_screenshot()
    {
        var saved = WpfClipboard.GetDataObject();
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
        var saved = WpfClipboard.GetDataObject();
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
    public void Exclude_format_present_excludes_regardless_of_history_flag()
    {
        var saved = WpfClipboard.GetDataObject();
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
    /// Restores whatever was on the clipboard before the test ran. Best-effort: a restore failure
    /// must never mask the test's real assertion result, and there is nothing further we can do if
    /// the clipboard refuses the write-back.
    /// </summary>
    private static void Restore(WpfIDataObject? saved)
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
