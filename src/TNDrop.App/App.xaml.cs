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
/// App entry point: single-instance guard, crash logging, i18n, and the tray icon that is
/// the whole UI surface until Task 9 adds the clipboard window.
/// </summary>
public partial class App : System.Windows.Application
{
    private const string Module = "App";
    private const string SingleInstanceMutexName = "Local\\TNDrop_SingleInstance";

    private Mutex? _singleInstanceMutex;
    private bool _mutexOwned;
    private TrayIcon? _trayIcon;

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

        // (6) Window creation is added in Task 9+.
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
