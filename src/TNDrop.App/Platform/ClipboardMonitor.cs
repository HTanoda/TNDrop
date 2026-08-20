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

    /// <summary>
    /// How long a <see cref="SuppressNext"/> request stays armed. It is a window, not a counter:
    /// a single WPF clipboard write raises WM_CLIPBOARDUPDATE more than once (OleSetClipboard and
    /// then OleFlushClipboard), so consuming exactly one notification would let the second one
    /// re-capture our own copy. The window also bounds the damage when a write fails and no
    /// notification arrives at all: it expires instead of swallowing the user's next real copy.
    /// </summary>
    private static readonly TimeSpan SuppressWindow = TimeSpan.FromSeconds(2);

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
    private long _suppressRequestedTicks;
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

        // Bound to the HwndSource's own dispatcher, so the tick (and therefore Captured) is
        // guaranteed to run on the very thread that owns the window and receives WndProc.
        _readTimer = new DispatcherTimer(DispatcherPriority.Normal, _source.Dispatcher)
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

    /// <summary>
    /// Ignores clipboard updates caused by our own copy-to-clipboard: every update arriving
    /// within <see cref="SuppressWindow"/> of this call is dropped. Not a one-shot counter --
    /// one WPF clipboard write raises the notification more than once.
    /// </summary>
    public void SuppressNext() => Interlocked.Exchange(ref _suppressRequestedTicks, DateTime.UtcNow.Ticks);

    /// <summary>True when AddClipboardFormatListener succeeded and has not been torn down.</summary>
    public bool IsListening => _listening;

    private bool IsSuppressed()
    {
        // Read WITHOUT clearing: the whole window must suppress, not just the first notification.
        var requestedTicks = Interlocked.Read(ref _suppressRequestedTicks);
        if (requestedTicks == 0L)
            return false;

        if (DateTime.UtcNow - new DateTime(requestedTicks, DateTimeKind.Utc) <= SuppressWindow)
            return true;

        // Expired: clear it, but only if nobody re-armed it in the meantime.
        Interlocked.CompareExchange(ref _suppressRequestedTicks, 0L, requestedTicks);
        return false;
    }

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

        if (IsSuppressed())
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

        // Re-check: state can change during the 50 ms delay (the user enables secret mode, a
        // fullscreen app starts, or we wrote to the clipboard ourselves just after this was armed).
        if (_disposed || IsSuppressed() || Paused || DateTime.UtcNow < IgnoreUntil)
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

        // Each step is isolated: unregistering the OS-level clipboard listener must not be
        // skippable because an earlier step threw (DispatcherTimer.Stop() calls VerifyAccess and
        // throws when Dispose runs off the owning thread). A leaked listener cannot be retried --
        // _disposed is already set and the HWND is about to go away.
        Step("remove clipboard listener", () =>
        {
            if (_listening && _source.Handle != IntPtr.Zero)
                RemoveClipboardFormatListener(_source.Handle);

            _listening = false;
        });

        Step("stop read timer", () =>
        {
            _readTimer.Stop();
            _readTimer.Tick -= OnReadTimerTick;
        });

        Step("remove hook", () => _source.RemoveHook(_hook));
        Step("dispose HwndSource", () => _source.Dispose());
    }

    private void Step(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _log?.Error(Module, $"dispose: {what} failed", ex);
        }
    }
}
