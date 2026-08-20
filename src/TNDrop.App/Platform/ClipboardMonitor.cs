using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TNDrop.Core;
using TNDrop.Services;

namespace TNDrop.Platform;

/// <summary>One clipboard snapshot as read off the clipboard, before it becomes a ClipItem.</summary>
public sealed class CapturedClip
{
    public ClipKind Kind { get; init; }
    public string? Text { get; init; }
    public string[]? Files { get; init; }
    public BitmapSource? Image { get; init; }
}

/// <summary>
/// Watches the clipboard through a hidden message window and AddClipboardFormatListener.
/// Must be constructed on the WPF UI thread; <see cref="Captured"/> is raised on that
/// same (Dispatcher) thread.
/// </summary>
public sealed class ClipboardMonitor : IDisposable
{
    private const string Module = "ClipboardMonitor";
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>
    /// Delay before reading: the source app may still hold the clipboard open when the
    /// notification arrives, and reading immediately loses the race.
    /// </summary>
    private static readonly TimeSpan ReadDelay = TimeSpan.FromMilliseconds(50);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private readonly FileLogger? _log;
    private readonly HwndSource _source;
    private readonly DispatcherTimer _readTimer;
    private readonly HwndSourceHook _hook;
    private int _suppressNext;
    private bool _listening;
    private bool _disposed;

    /// <summary>Raised on the Dispatcher thread once a clipboard change yielded usable content.</summary>
    public event EventHandler<CapturedClip>? Captured;

    /// <summary>True while capture is disabled (secret mode / fullscreen).</summary>
    public bool Paused { get; set; }

    /// <summary>
    /// UTC instant before which clipboard updates are dropped. Used to swallow the bogus
    /// clipboard notifications Windows emits around resume-from-sleep and unlock.
    /// </summary>
    public DateTime IgnoreUntil { get; set; }

    public ClipboardMonitor(FileLogger? log)
    {
        _log = log;

        var parameters = new HwndSourceParameters("TNDropClipWatcher")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = WS_EX_TOOLWINDOW,
        };

        _source = new HwndSource(parameters);
        _hook = WndProc;
        _source.AddHook(_hook);

        // Same thread as the HwndSource, so the tick (and therefore Captured) stays on the UI thread.
        _readTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher.CurrentDispatcher)
        {
            Interval = ReadDelay,
        };
        _readTimer.Tick += OnReadTimerTick;

        if (AddClipboardFormatListener(_source.Handle))
        {
            _listening = true;
        }
        else
        {
            _log?.Error(Module,
                $"AddClipboardFormatListener failed (Win32 error {Marshal.GetLastWin32Error()})");
        }
    }

    /// <summary>Ignores exactly one upcoming clipboard update (our own copy-to-clipboard).</summary>
    public void SuppressNext() => Interlocked.Exchange(ref _suppressNext, 1);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_CLIPBOARDUPDATE)
            return IntPtr.Zero;

        handled = true;
        OnClipboardUpdate();
        return IntPtr.Zero;
    }

    private void OnClipboardUpdate()
    {
        if (_disposed)
            return;

        if (Interlocked.Exchange(ref _suppressNext, 0) == 1)
            return;

        if (Paused)
            return;

        if (DateTime.UtcNow < IgnoreUntil)
            return;

        // Restart rather than start: bursts of updates from one copy collapse into one read.
        _readTimer.Stop();
        _readTimer.Start();
    }

    private void OnReadTimerTick(object? sender, EventArgs e)
    {
        _readTimer.Stop();

        if (_disposed)
            return;

        CapturedClip? clip;
        try
        {
            clip = ClipboardIo.ReadCurrent(_log);
        }
        catch (Exception ex)
        {
            _log?.Error(Module, "clipboard read failed", ex);
            return;
        }

        if (clip is null)
            return;

        try
        {
            Captured?.Invoke(this, clip);
        }
        catch (Exception ex)
        {
            _log?.Error(Module, "Captured handler threw", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            _readTimer.Stop();
            _readTimer.Tick -= OnReadTimerTick;

            if (_listening && _source.Handle != IntPtr.Zero)
            {
                RemoveClipboardFormatListener(_source.Handle);
                _listening = false;
            }

            _source.RemoveHook(_hook);
            _source.Dispose();
        }
        catch (Exception ex)
        {
            _log?.Error(Module, "dispose failed", ex);
        }
    }
}
