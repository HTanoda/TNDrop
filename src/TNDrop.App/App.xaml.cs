using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
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

    public static string DataDir { get; private set; } = string.Empty;

    public static ItemStore Store { get; private set; } = null!;

    public static SettingsStore SettingsStore { get; private set; } = null!;

    public static AppSettings Settings { get; private set; } = null!;

    public static SoundService Sounds { get; private set; } = null!;

    public static ClipboardMonitor Monitor { get; private set; } = null!;

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
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Error(Module, "Startup failed; shutting down", ex);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
        SaveSettings();
    }

    private void OnOpenSettingsRequested()
    {
        // 設定ウィンドウは Task 9 以降で実装する。
    }

    private void OnExitRequested() => Shutdown();

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
