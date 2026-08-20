using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using TNDrop.Core;
using TNDrop.Platform;
using TNDrop.Resources;
using TNDrop.Services;
using TNDrop.UI;

namespace TNDrop;

/// <summary>
/// App entry point: single-instance guard, crash logging, i18n, the tray icon, and the two
/// always-alive windows (edge trigger band and the sliding shelf).
/// </summary>
public partial class App : System.Windows.Application
{
    private const string Module = "App";
    private const string SingleInstanceMutexName = "Local\\TNDrop_SingleInstance";

    private Mutex? _singleInstanceMutex;
    private bool _mutexOwned;
    private TrayIcon? _trayIcon;
    private ShelfWindow? _shelf;
    private EdgeTriggerWindow? _edgeTrigger;
    private CapturePipeline? _pipeline;
    private AutoDeleteService? _autoDelete;
    private FullscreenDetector? _fullscreenDetector;

    /// <summary>
    /// How long a resume-from-sleep or unlock keeps <see cref="ClipboardMonitor.IgnoreUntil"/>
    /// in the future. Windows fires bogus clipboard-format-listener notifications for a moment
    /// around both events; this window swallows them so a stale clipboard doesn't get re-captured
    /// as if the user had just copied it.
    /// </summary>
    private static readonly TimeSpan WakeIgnoreWindow = TimeSpan.FromSeconds(2);

    public static string DataDir { get; private set; } = string.Empty;

    public static ItemStore Store { get; private set; } = null!;

    public static SettingsStore SettingsStore { get; private set; } = null!;

    public static AppSettings Settings { get; private set; } = null!;

    public static SoundService Sounds { get; private set; } = null!;

    public static ClipboardMonitor Monitor { get; private set; } = null!;

    /// <summary>
    /// The one long-lived capture indicator overlay. Static because the shelf's click-to-copy
    /// path (see ShelfWindow) has to flash the very same overlay a background capture does -- the
    /// confirmation for "I put this on the clipboard" must be indistinguishable from the one for
    /// "TNDrop captured this". Null until OnStartup gets that far (and in the designer / tests),
    /// unlike the non-null-by-contract statics above, so every caller must null-check.
    /// </summary>
    public static IndicatorWindow? Indicator { get; private set; }

    public static void SaveSettings() => SettingsStore.Save(Settings);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // (1) Single instance: second launch shuts down silently, no window, no MessageBox.
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        _mutexOwned = createdNew;
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        // (2) DataDir + logging + crash handlers, before anything else can throw.
        DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TNDrop");
        Directory.CreateDirectory(DataDir);

        FileLogger.Instance = new FileLogger(Path.Combine(DataDir, "logs"));
        FileLogger.Instance.Info(Module, "TNDrop starting up");

        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // (3)-(6) run under a single guard: a throw anywhere in here would otherwise be
        // caught by DispatcherUnhandledException (WPF pumps OnStartup on the Dispatcher),
        // which sets e.Handled = true and lets OnStartup return early -- with the mutex
        // already held, no tray icon, and ShutdownMode="OnExplicitShutdown", that would
        // leave an unkillable-by-normal-means headless zombie process. Catch here instead
        // and shut down explicitly so OnExit still runs and the process actually exits.
        try
        {
            // (3) Settings + UI culture, before any UI string is read.
            SettingsStore = new SettingsStore(DataDir);
            Settings = SettingsStore.Load();
            ApplyUiCulture(Settings.Language);

            // Self-heal: the exe may have moved (reinstall, drive letter change) since the
            // registry Run value was written, so re-derive it from the *current*
            // Environment.ProcessPath rather than trusting whatever is already there. Only
            // touches the registry when the two disagree, so a fresh profile with
            // AutoStartEnabled still false does not gain an unwanted Run entry.
            if (Settings.AutoStartEnabled != AutoStart.IsEnabled())
            {
                AutoStart.SetEnabled(Settings.AutoStartEnabled);
            }

            // (4) Clipboard history store.
            Store = new ItemStore(DataDir);
            Store.Load();

            // (5) Tray / monitor / sound.
            _trayIcon = new TrayIcon();
            _trayIcon.SetHoverEnabled(Settings.HoverEnabled);
            _trayIcon.SetIncognito(Settings.IncognitoMode);
            _trayIcon.HoverEnabledChanged += OnHoverEnabledChanged;
            _trayIcon.IncognitoChanged += OnIncognitoChanged;
            _trayIcon.OpenSettingsRequested += OnOpenSettingsRequested;
            _trayIcon.ExitRequested += OnExitRequested;

            Sounds = new SoundService(() => Settings.SoundsEnabled);

            Monitor = new ClipboardMonitor(FileLogger.Instance);
            Monitor.Paused = Settings.IncognitoMode;

            // Capture pipeline: its own ThumbnailService instance (separate from the one
            // ShelfViewModel builds for reading/rendering) since SaveImage is the only method the
            // pipeline calls and that method doesn't touch the read-side decode cache, so sharing
            // an instance would buy nothing but a shared constructor dependency.
            _pipeline = new CapturePipeline(Store, new ThumbnailService(Store.BlobsDir), () => Settings);

            Indicator = new IndicatorWindow();
            Indicator.Show();

            Monitor.Captured += OnClipboardCaptured;

            if (Store.LoadFailed)
            {
                _trayIcon.ShowBalloon(Strings.AppName, Strings.StoreLoadFailed);
            }

            // (6) Shelf + edge trigger. Created hidden; the trigger band is only shown while
            // hover-to-open is enabled.
            _shelf = new ShelfWindow();
            _edgeTrigger = new EdgeTriggerWindow();

            _shelf.ApplySettings(Settings);
            _shelf.IsVisibleChanged += OnShelfVisibleChanged;

            _edgeTrigger.ApplySettings(Settings);
            _edgeTrigger.SetHintVisible(Settings.EdgeHintEnabled);
            _edgeTrigger.Triggered += OnEdgeTriggered;

            if (Settings.HoverEnabled)
            {
                _edgeTrigger.Show();
            }

            // (7) Auto-delete: purge once immediately (stale items left over from a previous
            // session that never ran long enough to hit the 10-minute tick), then start the
            // recurring cycle. Off (the default) makes RunOnce/the tick a no-op either way.
            _autoDelete = new AutoDeleteService(Store, () => Settings.AutoDelete);
            var purgedAtStartup = _autoDelete.RunOnce();
            if (purgedAtStartup > 0)
            {
                FileLogger.Instance?.Info(Module, $"startup auto-delete purged {purgedAtStartup} stale item(s)");
            }

            _autoDelete.Start();

            // (8) Fullscreen: while a fullscreen/presentation app owns the screen, hovering the
            // edge would fight it for the pointer, so hide the trigger band (and with it the edge
            // hint). Capture itself keeps running -- this only stops the hover affordance, exactly
            // like the reference app. Restored on exit from fullscreen only if hover-to-open is still the
            // user's setting.
            _fullscreenDetector = new FullscreenDetector();
            _fullscreenDetector.Changed += OnFullscreenChanged;

            // (9) Sleep/unlock: Windows re-broadcasts a stale clipboard around both events, which
            // would otherwise be captured as if freshly copied. SystemEvents is a static,
            // process-wide publisher -- these handlers are unsubscribed in OnExit or they would
            // outlive this App instance and, in a test host that creates/tears down multiple App
            // objects, throw/log against a torn-down Monitor.
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;

            // (10) Monitor configuration changes (unplug/replug, resolution change): re-place the
            // always-alive windows. MonitorGeometry.Resolve already falls back to the primary
            // monitor when Settings.MonitorDeviceName is no longer present, and Settings itself is
            // deliberately left untouched here so reconnecting the original monitor restores it
            // automatically instead of the fallback sticking. The indicator overlay is not listed
            // here: it re-resolves its monitor on every Flash() call instead of holding a placement
            // to go stale.
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Error(Module, "Startup failed; shutting down", ex);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Static, process-wide events: must be unsubscribed explicitly. Left subscribed, they
        // would keep this (about-to-be-collected) App instance alive from SystemEvents' side and,
        // for anything that fires after Monitor is disposed below, throw/log against a dead
        // object -- neither of which "the process is exiting anyway" makes harmless, since
        // SystemEvents' registration outlives normal GC.
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        _fullscreenDetector?.Dispose();
        _autoDelete?.Dispose();

        Store?.Save();
        _trayIcon?.Dispose();
        Monitor?.Dispose();
        ReleaseSingleInstanceMutex();

        FileLogger.Instance?.Info(Module, "TNDrop exiting");

        base.OnExit(e);
    }

    private static void ApplyUiCulture(string language)
    {
        CultureInfo culture;
        try
        {
            culture = new CultureInfo(language);
        }
        catch (CultureNotFoundException ex)
        {
            FileLogger.Instance?.Warn(Module, $"Unknown Language setting '{language}'; falling back to ja: {ex.Message}");
            culture = new CultureInfo("ja");
        }

        Thread.CurrentThread.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    private void ReleaseSingleInstanceMutex()
    {
        if (_singleInstanceMutex is null)
        {
            return;
        }

        if (_mutexOwned)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (Exception ex)
            {
                FileLogger.Instance?.Warn(Module, $"ReleaseMutex failed: {ex.Message}");
            }
        }

        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;
    }

    private void OnHoverEnabledChanged(bool value)
    {
        Settings.HoverEnabled = value;
        SaveSettings();

        if (value)
        {
            _edgeTrigger?.Show();
        }
        else
        {
            _edgeTrigger?.Hide();
            _shelf?.SlideOut();
        }
    }

    private void OnEdgeTriggered()
    {
        if (!Settings.HoverEnabled)
        {
            return;
        }

        _shelf?.SlideIn();
    }

    /// <summary>
    /// The trigger band sits on top of the strip of screen edge the shelf covers when it is out.
    /// Leaving it visible would let it swallow the pointer at the very edge, which reads to the
    /// shelf as "pointer left" and retracts it under the user's cursor. So: shelf out, band away.
    /// </summary>
    private void OnShelfVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_edgeTrigger is null)
        {
            return;
        }

        if (_shelf is not null && _shelf.IsVisible)
        {
            _edgeTrigger.Hide();
        }
        else if (Settings.HoverEnabled)
        {
            _edgeTrigger.Show();
        }
    }

    private void OnIncognitoChanged(bool value)
    {
        Settings.IncognitoMode = value;
        Monitor.Paused = value;

        // The tray's own Click handler already flipped the checkbox before raising this event;
        // SetIncognito is called again here purely for its tooltip-text side effect (idempotent
        // on the checkbox itself), so the notification-area tooltip reflects secret mode without
        // the user having to open the menu to tell.
        _trayIcon?.SetIncognito(value);

        SaveSettings();
    }

    /// <summary>
    /// The capture pipeline owns TryAdd + Save; this handler only decides what happens for the
    /// user once that succeeds -- flash the edge and play the capture cue. A dedup no-op (the
    /// same text copied twice in a row) yields neither.
    /// </summary>
    private void OnClipboardCaptured(object? sender, CapturedClip clip) => NotifyManualCapture(clip);

    /// <summary>
    /// Single entry point for a capture that did not arrive via the clipboard-change
    /// notification -- currently: a drop onto the shelf from another app (Task 13). Routes
    /// through the very same <see cref="CapturePipeline"/> as a real clipboard capture, so
    /// dedup/stacking/persistence behave identically, and reuses the same success confirmation
    /// (indicator flash + capture sound) instead of a second copy of that logic drifting out of
    /// sync with <see cref="OnClipboardCaptured"/>.
    /// <para>Static, following the same "reach the running App instance via
    /// <see cref="System.Windows.Application.Current"/>" pattern ShelfWindow already uses for
    /// <c>App.Store</c> / <c>App.Indicator</c> / <c>App.Sounds</c>; returns false harmlessly
    /// (rather than throwing) when called before startup finishes or from a test host with no
    /// live App instance.</para>
    /// </summary>
    public static bool NotifyManualCapture(CapturedClip clip)
    {
        if (System.Windows.Application.Current is not App app || app._pipeline is null || Indicator is null)
        {
            return false;
        }

        if (!app._pipeline.Process(clip))
        {
            return false;
        }

        Indicator.Flash(Settings.IndicatorStyle, Settings.Edge);
        Sounds.PlayCapture();
        return true;
    }

    private void OnOpenSettingsRequested()
    {
        // 設定ウィンドウは Task 9 以降で実装する。
    }

    private void OnExitRequested() => Shutdown();

    /// <summary>
    /// Fullscreen/presentation mode started or ended. Only the hover affordance is touched --
    /// capture keeps running the whole time, matching the reference app's behavior -- and exiting
    /// fullscreen restores the trigger band only if hover-to-open is still the user's setting
    /// (mirrors <see cref="OnHoverEnabledChanged"/>'s "true" branch).
    /// </summary>
    private void OnFullscreenChanged(object? sender, bool isFullscreen)
    {
        if (isFullscreen)
        {
            _edgeTrigger?.Hide();
            FileLogger.Instance?.Info(Module, "fullscreen detected; hiding the edge trigger (capture continues)");
        }
        else if (Settings.HoverEnabled)
        {
            _edgeTrigger?.Show();
            FileLogger.Instance?.Info(Module, "fullscreen ended; restoring the edge trigger");
        }
    }

    /// <summary>
    /// Resume from sleep: Windows re-broadcasts whatever was last on the clipboard for a moment
    /// after waking, which would otherwise look exactly like the user copying it just now.
    /// Raised by <see cref="SystemEvents"/> on its own background thread, not the Dispatcher, so
    /// the actual write is marshaled -- <see cref="ClipboardMonitor.IgnoreUntil"/> is a plain,
    /// unlocked property and Monitor is torn down on the UI thread during shutdown.
    /// </summary>
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            Monitor.IgnoreUntil = DateTime.UtcNow + WakeIgnoreWindow;
            FileLogger.Instance?.Info(Module, "resumed from sleep; ignoring clipboard updates for 2s");
        });
    }

    /// <summary>Session unlock: same bogus-clipboard-beacon problem as resume-from-sleep.</summary>
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason != SessionSwitchReason.SessionUnlock)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            Monitor.IgnoreUntil = DateTime.UtcNow + WakeIgnoreWindow;
            FileLogger.Instance?.Info(Module, "session unlocked; ignoring clipboard updates for 2s");
        });
    }

    /// <summary>
    /// Monitor(s) added/removed/reconfigured. Re-places the shelf and the trigger band; the
    /// indicator overlay needs no equivalent call because it re-resolves its monitor on every
    /// <see cref="IndicatorWindow.Flash"/> instead of caching a placement. Also raised off the
    /// Dispatcher thread -- see <see cref="OnPowerModeChanged"/>.
    /// </summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        RunOnUiThread(() =>
        {
            FileLogger.Instance?.Info(Module, "display settings changed; re-applying window placement");
            _shelf?.ApplySettings(Settings);
            _edgeTrigger?.ApplySettings(Settings);
        });
    }

    /// <summary>
    /// Marshals onto the Dispatcher this Application owns. <see cref="SystemEvents"/> callbacks
    /// arrive on its own internal thread, not the UI thread, so anything they do that touches a
    /// WPF window (ApplySettings) or should serialize with UI-thread work must go through here
    /// rather than running inline. Best-effort: if the dispatcher is already shutting down (app
    /// exiting while a SystemEvents callback is in flight), log and drop it rather than throw.
    /// </summary>
    private void RunOnUiThread(Action action)
    {
        try
        {
            Dispatcher.BeginInvoke(action);
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Warn(Module, $"could not dispatch to the UI thread: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies an autostart change: updates the setting, writes the registry Run value, and
    /// persists. Not called anywhere yet -- Task 17's settings window is the intended caller, so
    /// this only wires the mechanism and keeps the startup self-heal (see OnStartup) as the sole
    /// active path until that UI exists.
    /// </summary>
    public static void SetAutoStartEnabled(bool enabled)
    {
        Settings.AutoStartEnabled = enabled;
        AutoStart.SetEnabled(enabled);
        SaveSettings();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        FileLogger.Instance?.Error(Module, "Unhandled dispatcher exception", e.Exception);
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        FileLogger.Instance?.Error(Module, "Unhandled AppDomain exception", e.ExceptionObject as Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        FileLogger.Instance?.Error(Module, "Unobserved task exception", e.Exception);
        e.SetObserved();
    }
}
