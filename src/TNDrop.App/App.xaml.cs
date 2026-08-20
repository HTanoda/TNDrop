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
    private SettingsWindow? _settingsWindow;

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

            // Text scale (Task 17): applied before any window is constructed so the very first
            // layout pass already reads the saved size instead of the App.xaml default and then
            // visibly resizing a moment later. TextScaleMap.Apply is the single place that turns
            // TextScale into point sizes -- SettingsWindow calls the very same method for a live
            // change, see its doc comment.
            TextScaleMap.Apply(Settings.TextScale, Resources);

            // Self-heal: compares against the exact stored value, not just whether one is
            // present. AutoStart.IsEnabled() alone would miss a STALE registry value -- the exe
            // moved (reinstall, drive letter change) since the Run value was written, so it still
            // points at the old path even though "a value is present" reads as already correct.
            // ExpectedCommand() is null when AutoStartEnabled is false, so this also catches (and
            // removes) a Run value that outlived the setting being turned off. A fresh profile
            // with AutoStartEnabled still false and no Run value compares null == null and skips
            // the registry write entirely.
            var expectedAutoStartCommand = Settings.AutoStartEnabled ? AutoStart.ExpectedCommand() : null;
            if (!string.Equals(expectedAutoStartCommand, AutoStart.GetStoredCommand(), StringComparison.Ordinal))
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
            _trayIcon.HoverEnabledChanged += SetHoverEnabled;
            // Subscribed directly to the static setter rather than through an instance wrapper:
            // SetIncognitoMode (below) is also SettingsWindow's call target for the same
            // checkbox, so routing the tray's own click through anything else would be a second,
            // driftable copy of "what changing incognito mode does".
            _trayIcon.IncognitoChanged += SetIncognitoMode;
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

    /// <summary>
    /// Turns hover-to-open on/off: persists the setting, shows/hides the edge trigger band (or
    /// slides the shelf back in/out) and syncs the tray checkbox. Single static call target
    /// (v1.1 Task C) for both the tray menu's own click (subscribed directly in OnStartup, above)
    /// and SettingsWindow's hover checkbox -- see <see cref="SetIncognitoMode"/>'s doc comment for
    /// the same "one entry point reached via Application.Current" rationale, which is what keeps
    /// "what turning hover on/off does" from having two copies that could drift apart.
    /// <para>KNOWN LIMIT: if SettingsWindow is open when the TRAY toggles this, its checkbox does
    /// not live-update (WireCheckBox only sets the box once, at construction) -- the same accepted
    /// limitation IncognitoCheckBox already has for the tray's own incognito item.</para>
    /// </summary>
    public static void SetHoverEnabled(bool value)
    {
        if (System.Windows.Application.Current is not App app)
        {
            return;
        }

        Settings.HoverEnabled = value;
        SaveSettings();

        if (value)
        {
            // Do NOT show while a fullscreen/presentation app is active: OnFullscreenChanged
            // already hid the trigger for a reason that has nothing to do with this setting, and
            // FullscreenDetector.Changed only fires on the true/false TRANSITION -- toggling
            // hover on (here, or via the tray, or now the settings window) while still fullscreen
            // would otherwise put the trigger back over the fullscreen app and leave it there
            // until the app is polled again, since no further Changed event is coming to hide it
            // a second time. Gated here rather than in FullscreenDetector itself, which only
            // tracks fullscreen state and has no opinion on hover.
            if (app._fullscreenDetector?.IsFullscreen != true)
            {
                app._edgeTrigger?.Show();
            }
        }
        else
        {
            app._edgeTrigger?.Hide();
            app._shelf?.SlideOut();
        }

        // Syncs the tray checkbox for every caller, including the tray's own click: TrayIcon's
        // SetHoverEnabled never re-raises HoverEnabledChanged (see its doc comment), so this is
        // idempotent when the tray was the caller and is what keeps the settings-window checkbox
        // change reflected on the tray menu the next time it opens.
        app._trayIcon?.SetHoverEnabled(value);
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

    /// <summary>
    /// Turns incognito mode on/off: pauses/resumes capture, syncs the tray checkbox (and its
    /// tooltip suffix) and persists. The single call target for both the tray menu's own click
    /// (subscribed directly in OnStartup) and SettingsWindow's checkbox -- see NotifyManualCapture
    /// for the same "one static entry point reached via Application.Current" pattern.
    /// </summary>
    public static void SetIncognitoMode(bool value)
    {
        if (System.Windows.Application.Current is not App app)
        {
            return;
        }

        Settings.IncognitoMode = value;
        Monitor.Paused = value;
        app._trayIcon?.SetIncognito(value);
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

    /// <summary>
    /// Opens the settings window, or re-activates the one already open. Single-instance rather
    /// than one-per-click: a second "設定..." click while the window is already up must bring the
    /// existing window forward (and restore it if minimized), not spawn a duplicate that would
    /// immediately fight the first one over every ApplySettings/SaveSettings call.
    /// </summary>
    private void OnOpenSettingsRequested()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();

            // The field must be cleared when the window closes (X button, Alt+F4) or the next
            // "設定..." click would Activate() a disposed window instead of creating a fresh one.
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        if (_settingsWindow.WindowState == WindowState.Minimized)
        {
            _settingsWindow.WindowState = WindowState.Normal;
        }

        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>
    /// Public static entry point (v1.1 Task C) so ShelfWindow's new header ⚙ button opens exactly
    /// the same settings window the tray's own "設定..." menu item does -- <see cref="OnOpenSettingsRequested"/>
    /// itself stays private and instance-scoped (it is also the tray's direct event handler; see
    /// OnStartup's <c>_trayIcon.OpenSettingsRequested += OnOpenSettingsRequested;</c>), so there is
    /// exactly one place that knows how to open/re-activate the single settings window instance.
    /// No-op (rather than throwing) before startup finishes or from a test host with no live App
    /// instance, matching every other <c>Application.Current as App</c> entry point in this file.
    /// </summary>
    public static void OpenSettingsWindow()
    {
        if (System.Windows.Application.Current is App app)
        {
            app.OnOpenSettingsRequested();
        }
    }

    private void OnExitRequested() => Shutdown();

    // ---- Settings window entry points (Task 17) --------------------------------------------
    //
    // Every one of these follows the same shape: mutate Settings, SaveSettings(), and push the
    // change out to whatever live window/service actually reads that setting -- resolving the
    // live App instance first wherever the propagation half needs an instance field
    // (_shelf/_edgeTrigger/_trayIcon), the same "Application.Current as App, no-op if there isn't
    // one" pattern NotifyManualCapture and SetIncognitoMode above already use, so the design-time
    // gap and any future test host stay just as safe as they are today. SettingsWindow never
    // touches Settings or the store directly -- these methods (plus SetAutoStartEnabled and
    // SetIncognitoMode above) are the ONLY place a settings change is written and propagated, so
    // no second copy of "what changing setting X does" can drift out of sync with this one.

    public static void SetSoundsEnabled(bool value)
    {
        Settings.SoundsEnabled = value;
        SaveSettings();
    }

    public static void SetAutoDelete(AutoDeletePolicy value)
    {
        Settings.AutoDelete = value;
        SaveSettings();
    }

    public static void SetMoveToTopOnCopy(bool value)
    {
        Settings.MoveToTopOnCopy = value;
        SaveSettings();
    }

    public static void SetRetractDelayMs(int value)
    {
        Settings.RetractDelayMs = value;
        SaveSettings();
        ReapplyPlacement();
    }

    public static void SetEdge(EdgeSide value)
    {
        Settings.Edge = value;
        SaveSettings();
        ReapplyPlacement();
    }

    public static void SetMonitorDeviceName(string? value)
    {
        Settings.MonitorDeviceName = value;
        SaveSettings();
        ReapplyPlacement();
    }

    public static void SetHotZonePercent(int value)
    {
        Settings.HotZonePercent = value;
        SaveSettings();
        ReapplyPlacement();
    }

    public static void SetTriggerProximityPx(int value)
    {
        Settings.TriggerProximityPx = value;
        SaveSettings();
        ReapplyPlacement();
    }

    public static void SetTriggerAlign(TriggerAlign value)
    {
        Settings.TriggerAlign = value;
        SaveSettings();
        ReapplyPlacement();
    }

    /// <summary>
    /// Swaps the App.xaml DynamicResource sizes via <see cref="TextScaleMap"/> -- see that
    /// class's doc comment for why this is the only place besides OnStartup that computes them.
    /// </summary>
    public static void SetTextScale(TextScale value)
    {
        Settings.TextScale = value;
        SaveSettings();

        if (System.Windows.Application.Current is App app)
        {
            TextScaleMap.Apply(value, app.Resources);
        }
    }

    public static void SetIndicatorStyle(IndicatorStyle value)
    {
        Settings.IndicatorStyle = value;
        SaveSettings();
    }

    /// <summary>
    /// UI language: saved but not applied live -- see <see cref="Strings"/>' class doc comment
    /// and <see cref="ApplyUiCulture"/>'s call site in OnStartup. SettingsWindow's own note tells
    /// the user the change needs a restart, so this method deliberately does not attempt one.
    /// </summary>
    public static void SetLanguage(string value)
    {
        Settings.Language = value;
        SaveSettings();
    }

    public static void SetEdgeHintEnabled(bool value)
    {
        Settings.EdgeHintEnabled = value;
        SaveSettings();

        if (System.Windows.Application.Current is App app)
        {
            app._edgeTrigger?.SetHintVisible(value);
        }
    }

    /// <summary>
    /// Re-applies Settings to the shelf and edge trigger windows -- the one place both
    /// <see cref="OnDisplaySettingsChanged"/> (monitor unplug/replug) and every position-affecting
    /// setter above call to push a geometry change out to the live windows, instead of each
    /// keeping its own copy of "re-place both windows, and don't let one's failure block the
    /// other's". Safe to call from the Dispatcher thread directly (every setter above already
    /// runs on it, since SettingsWindow's controls raise their events there); the one caller that
    /// is NOT already on the Dispatcher (<see cref="OnDisplaySettingsChanged"/>) wraps its own
    /// call in <see cref="RunOnUiThread"/> instead of this method doing so itself.
    /// </summary>
    public static void ReapplyPlacement()
    {
        if (System.Windows.Application.Current is not App app)
        {
            return;
        }

        try
        {
            app._shelf?.ApplySettings(Settings);
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Error(Module, "failed to re-apply settings to the shelf", ex);
        }

        try
        {
            app._edgeTrigger?.ApplySettings(Settings);
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Error(Module, "failed to re-apply settings to the edge trigger", ex);
        }
    }

    /// <summary>
    /// Fullscreen/presentation mode started or ended. Only the hover affordance is touched --
    /// capture keeps running the whole time, matching the reference app's behavior -- and exiting
    /// fullscreen restores the trigger band only if hover-to-open is still the user's setting
    /// (mirrors <see cref="OnHoverEnabledChanged"/>'s "true" branch).
    /// <para>The "false" (exit-fullscreen) branch below does NOT need to re-check
    /// <see cref="FullscreenDetector.IsFullscreen"/> the way <see cref="OnHoverEnabledChanged"/>
    /// checks it before showing: by the time this handler runs, <c>_fullscreenDetector.IsFullscreen</c>
    /// has already been updated to false (see <see cref="FullscreenDetector.OnTick"/>, which sets
    /// the property before raising <see cref="FullscreenDetector.Changed"/>), so there is nothing
    /// left to gate against -- this handler IS the transition, not a caller reacting to some other
    /// setting after the fact.</para>
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
    /// <para>Raised by <see cref="SystemEvents"/> on its own background thread, not the
    /// Dispatcher -- but the write below runs directly on THAT thread, synchronously, rather than
    /// being marshaled through <see cref="RunOnUiThread"/>: marshaling would queue it behind
    /// whatever is already in the Dispatcher queue, and a spurious post-wake clipboard
    /// notification racing in on the Dispatcher's own timer could win that race and land before
    /// <see cref="ClipboardMonitor.IgnoreUntil"/> was actually updated. Safe to write from here
    /// because <see cref="ClipboardMonitor.IgnoreUntil"/> is now backed by an Interlocked tick
    /// count rather than a plain property -- see its doc comment.</para>
    /// </summary>
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume)
        {
            return;
        }

        Monitor.IgnoreUntil = DateTime.UtcNow + WakeIgnoreWindow;
        FileLogger.Instance?.Info(Module, "resumed from sleep; ignoring clipboard updates for 2s");
    }

    /// <summary>
    /// Session unlock: same bogus-clipboard-beacon problem as resume-from-sleep, same direct
    /// (not marshaled) write -- see <see cref="OnPowerModeChanged"/>.
    /// </summary>
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason != SessionSwitchReason.SessionUnlock)
        {
            return;
        }

        Monitor.IgnoreUntil = DateTime.UtcNow + WakeIgnoreWindow;
        FileLogger.Instance?.Info(Module, "session unlocked; ignoring clipboard updates for 2s");
    }

    /// <summary>
    /// Monitor(s) added/removed/reconfigured. Re-places the shelf and the trigger band; the
    /// indicator overlay needs no equivalent call because it re-resolves its monitor on every
    /// <see cref="IndicatorWindow.Flash"/> instead of caching a placement. Raised off the
    /// Dispatcher thread (same as <see cref="OnPowerModeChanged"/>), so -- unlike that handler --
    /// this one DOES need <see cref="RunOnUiThread"/>: ApplySettings touches WPF window
    /// properties (Width/Left/Top etc.), which is not safe off the Dispatcher thread.
    /// </summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        RunOnUiThread(() =>
        {
            FileLogger.Instance?.Info(Module, "display settings changed; re-applying window placement");
            ReapplyPlacement();
        });
    }

    /// <summary>
    /// Marshals onto the Dispatcher this Application owns. Used by
    /// <see cref="OnDisplaySettingsChanged"/>, whose work touches WPF windows and so must run on
    /// the Dispatcher thread; NOT used by <see cref="OnPowerModeChanged"/>/<see cref="OnSessionSwitch"/>,
    /// which write a cross-thread-safe field directly and deliberately skip this queue (see their
    /// doc comments) to avoid losing a race with a spurious clipboard notification. Best-effort:
    /// if the dispatcher is already shutting down (app exiting while a SystemEvents callback is in
    /// flight), log and drop it rather than throw.
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
    /// persists. Called by SettingsWindow's checkbox (Task 17); the startup self-heal in
    /// OnStartup still runs on every launch to correct drift this method cannot see (a
    /// moved/reinstalled exe changing the expected registry value between runs).
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
