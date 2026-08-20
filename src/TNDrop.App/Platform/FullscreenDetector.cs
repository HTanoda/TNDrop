using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using TNDrop.Services;

namespace TNDrop.Platform;

/// <summary>
/// Polls Windows' "should I stay quiet?" state so TNDrop can suppress capture and toasts
/// while the user is presenting, gaming, or otherwise in a do-not-disturb situation.
/// Construct on the WPF UI thread; <see cref="Changed"/> is raised on that thread.
/// </summary>
public sealed class FullscreenDetector : IDisposable
{
    private const string Module = "FullscreenDetector";

    private enum UserNotificationState
    {
        NotPresent = 1,
        Busy = 2,                  // QUNS_BUSY: a full-screen non-D3D app is running
        RunningD3dFullScreen = 3,  // QUNS_RUNNING_D3D_FULL_SCREEN
        PresentationMode = 4,      // QUNS_PRESENTATION_MODE
        AcceptsNotifications = 5,
        QuietTime = 6,
        App = 7,
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out UserNotificationState state);

    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);

    private readonly DispatcherTimer _timer;
    private bool _loggedFailure;
    private bool _disposed;

    public bool IsFullscreen { get; private set; }

    /// <summary>Raised only on transitions, never on every poll.</summary>
    public event EventHandler<bool>? Changed;

    public FullscreenDetector(TimeSpan pollInterval = default)
    {
        if (pollInterval <= TimeSpan.Zero)
            pollInterval = DefaultInterval;

        IsFullscreen = Query();

        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
        {
            Interval = pollInterval,
        };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed)
            return;

        var current = Query();
        if (current == IsFullscreen)
            return;

        IsFullscreen = current;

        try
        {
            Changed?.Invoke(this, current);
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Error(Module, "Changed handler threw", ex);
        }
    }

    private bool Query()
    {
        try
        {
            var hr = SHQueryUserNotificationState(out var state);
            if (hr < 0)
            {
                LogFailureOnce($"SHQueryUserNotificationState returned HRESULT 0x{hr:X8}", null);
                return false;
            }

            return state is UserNotificationState.Busy
                or UserNotificationState.RunningD3dFullScreen
                or UserNotificationState.PresentationMode;
        }
        catch (Exception ex)
        {
            LogFailureOnce("SHQueryUserNotificationState failed", ex);
            return false;
        }
    }

    // Polling runs every couple of seconds; a persistent failure must not flood the log.
    private void LogFailureOnce(string message, Exception? ex)
    {
        if (_loggedFailure)
            return;

        _loggedFailure = true;

        if (ex is null)
            FileLogger.Instance?.Warn(Module, message + " (further failures not logged)");
        else
            FileLogger.Instance?.Error(Module, message + " (further failures not logged)", ex);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
