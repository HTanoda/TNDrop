using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TNDrop.Core;
using TNDrop.Platform;
using TNDrop.Services;

namespace TNDrop.UI;

/// <summary>
/// The invisible hover band pinned to the configured screen edge. Its only job is to notice the
/// pointer arriving and raise <see cref="Triggered"/>; the shelf itself is a separate window so
/// that showing the shelf never resizes or moves the thing the user is pointing at.
/// </summary>
public partial class EdgeTriggerWindow : Window
{
    private const string Module = "EdgeTriggerWindow";

    /// <summary>Placement passes allowed per ApplySettings call. See the re-entrancy latch there.</summary>
    private const int MaxPlacementPasses = 2;

    /// <summary>Poll rate for the proximity hint (v1.2 Task E). Cheap -- one Win32 cursor read
    /// (<see cref="MonitorGeometry.CursorDip"/>) plus pure arithmetic against the geometry cached
    /// by <see cref="Place"/> -- so 250ms costs nothing worth avoiding, but frequent enough that
    /// the beacon feels responsive as the pointer approaches. It does NOT re-resolve the monitor
    /// or recompute the trigger rect on every tick; see the fields below and Place's doc comment.</summary>
    private static readonly TimeSpan HintPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Alpha for the persistent hover beacon's mid-stop (v1.4.1 Task A). This lamp is
    /// NOT user-configurable (unlike IndicatorWindow's flash, which reads
    /// Settings.IndicatorColor/IndicatorOpacityPercent via ApplyPalette) - it always uses this
    /// fixed alpha over IndicatorPalette's DEFAULT base RGB, which is what keeps it dimmer than
    /// IndicatorWindow's momentary flash while still sitting in the same color family.</summary>
    private const byte HintBeaconAlpha = 0xCC;

    private AppSettings? _settings;
    private bool _applying;
    private bool _reapplyRequested;

    private readonly DispatcherTimer _hintTimer;
    private bool _hintFeatureEnabled;

    /// <summary>Last state actually applied via <see cref="SetHintVisible"/> -- lets the 250ms poll
    /// call SetHintVisible(show) every tick without restarting the fade/breathing animation on every
    /// single tick where the state hasn't changed (see <see cref="OnHintTimerTick"/>).</summary>
    private bool _hintVisible;

    /// <summary>Geometry snapshot for the proximity-hint timer, refreshed only when <see cref="Place"/>
    /// runs (ApplySettings / DPI change / display-settings change) -- NOT on every 250ms tick. See
    /// <see cref="OnHintTimerTick"/>.</summary>
    private MonitorGeometry.WorkArea? _hintArea;
    private ShelfPlacement.Rect _hintWorkArea;
    private ShelfPlacement.Rect _hintTriggerRect;

    /// <summary>Raised when the pointer enters the band.</summary>
    public event Action? Triggered;

    /// <summary>
    /// Raised when an in-flight OLE drag enters the band (v1.2 Task B). Same request as
    /// <see cref="Triggered"/> -- "open the shelf" -- but a distinct event so App can open it in
    /// the drag-aware way (ShelfWindow.SlideInForDrag) while still passing through exactly the
    /// same HoverEnabled/fullscreen gate; see App.RequestShelfFromEdge.
    /// </summary>
    public event Action? DragTriggered;

    public EdgeTriggerWindow()
    {
        InitializeComponent();

        // v1.4.1 Task A (v1.5: source retired from IndicatorBrightness to IndicatorPalette when
        // IndicatorBrightness was removed): the beacon's mid-stop color comes from
        // IndicatorPalette's DEFAULT base RGB at a fixed alpha -- one source for the brand hue
        // instead of a second hand-picked hex literal in XAML that could drift from it. This
        // lamp intentionally does NOT read Settings.IndicatorColor (unlike IndicatorWindow's
        // flash) - see HintBeaconAlpha's doc comment; see the XAML comment above HintBeacon for
        // the fuller rationale.
        IndicatorPalette.TryParseHex(IndicatorPalette.DefaultColorHex, out var hintBaseColor);
        HintBeaconMidStop.Color = System.Windows.Media.Color.FromArgb(
            HintBeaconAlpha, hintBaseColor.R, hintBaseColor.G, hintBaseColor.B);

        MouseEnter += (_, _) => Triggered?.Invoke();
        DpiChanged += OnDpiChanged;

        // Drag-hover open (v1.2 Task B). AllowDrop="True" is set in XAML; these two are the whole
        // drop-target contract this window needs.
        //
        // BOTH, not just DragEnter: DragEnter fires once per arrival, and a drag that was already
        // inside the band's rectangle when the OLE session started (the user presses the mouse down
        // right at the screen edge) can produce a DragOver without a preceding DragEnter. Raising
        // on both is safe because the request is idempotent -- App gates it, and ShelfWindow.SlideIn
        // on an already-shown shelf just re-runs the animation from where it is.
        DragEnter += OnBandDrag;
        DragOver += OnBandDrag;

        // Proximity hint (v1.2 Task E): the timer only ever runs while this window is visible.
        // App only calls Show() on this window while HoverEnabled is on, no fullscreen app is up
        // and the shelf itself is not out (see App.SetHoverEnabled/OnShelfVisibleChanged/
        // OnFullscreenChanged) -- so IsVisible already IS "HoverEnabled && !fullscreen && shelf
        // hidden" with no second copy of that gate needed here. See SetHintEnabled.
        IsVisibleChanged += (_, _) => UpdateHintTimerState();

        _hintTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
        {
            Interval = HintPollInterval,
        };
        _hintTimer.Tick += OnHintTimerTick;

        // Create the HWND now. ApplySettings runs before the window is ever shown and its
        // device-pixel snap needs a handle; without this the first placement silently skips it.
        new WindowInteropHelper(this).EnsureHandle();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Must run after the HWND exists: keeps the band out of Alt+Tab and stops it taking focus.
        WindowStyles.MakeToolWindowNoActivate(this);
    }

    /// <summary>Recomputes size and position from the settings, resolving the target monitor and its DPI.</summary>
    public void ApplySettings(AppSettings s)
    {
        if (s is null)
            return;

        _settings = s;

        if (_applying)
        {
            // WM_DPICHANGED is delivered synchronously from inside Place's SetWindowPos call, so
            // Place can re-enter ApplySettings through DpiChanged. Dropping that re-entrant call
            // would leave the rect the OS suggested for the DPI change in place instead of ours.
            // Latch it and re-run once the outer pass unwinds.
            _reapplyRequested = true;
            FileLogger.Instance?.Info(Module, "placement re-entered during placement; will re-apply");
            return;
        }

        _applying = true;
        try
        {
            var passes = 0;
            do
            {
                _reapplyRequested = false;
                Place(_settings);
            }
            while (_reapplyRequested && ++passes < MaxPlacementPasses);
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Error(Module, "Failed to place the edge trigger band", ex);
        }
        finally
        {
            _reapplyRequested = false;
            _applying = false;
        }
    }

    private void Place(AppSettings s)
    {
        var area = MonitorGeometry.Resolve(s.MonitorDeviceName, this);
        var workArea = new ShelfPlacement.Rect(area.X, area.Y, area.W, area.H);
        var rect = ShelfPlacement.TriggerRect(
            workArea, s.Edge, s.TriggerProximityPx, s.HotZonePercent, s.TriggerAlign);

        Width = rect.W;
        Height = rect.H;
        Left = rect.X;
        Top = rect.Y;

        HintBeacon.HorizontalAlignment = s.Edge == EdgeSide.Left
            ? System.Windows.HorizontalAlignment.Left
            : System.Windows.HorizontalAlignment.Right;

        MonitorGeometry.SnapToDeviceRect(this,
            rect.X * area.ScaleX, rect.Y * area.ScaleY,
            rect.W * area.ScaleX, rect.H * area.ScaleY);

        // Cache for OnHintTimerTick (v1.2 Task E, fix round 1): the tick used to call
        // MonitorGeometry.Resolve (a Screen.AllScreens allocation plus a GetDpiForMonitor
        // P/Invoke) and recompute TriggerRect on every single 250ms poll just to read the cursor
        // against them. Place() already re-runs on every actual geometry change -- ApplySettings,
        // DPI change, display-settings change -- so caching here means the tick only ever needs a
        // fresh Cursor.Position against whatever this snapshot last computed.
        _hintArea = area;
        _hintWorkArea = workArea;
        _hintTriggerRect = rect;

        FileLogger.Instance?.Info(Module,
            $"placed on {area.DeviceName} scale {area.ScaleX:0.##} at " +
            $"({rect.X:0},{rect.Y:0}) {rect.W:0}x{rect.H:0} DIP");
    }

    /// <summary>
    /// A drag is over the band: ask for the shelf, and refuse the payload.
    ///
    /// <para><c>Effects = None</c> is the point, not an oversight. The band is 3px of screen edge
    /// with nothing behind it; the thing that accepts the drop is the SHELF this request is about
    /// to open, which has its own drop handling (Task 13) and its own accept affordance. Offering
    /// Copy here would show the user a drop cursor over a window that would silently swallow their
    /// files.</para>
    ///
    /// <para><c>Handled = true</c> stops the same drag from also being answered by the Grid/beacon
    /// underneath, which would leave the last writer deciding what the cursor says.</para>
    /// </summary>
    private void OnBandDrag(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = System.Windows.DragDropEffects.None;
        e.Handled = true;

        DragTriggered?.Invoke();
    }

    /// <summary>Shows or hides the beacon that hints where the shelf lives. The display primitive:
    /// callers that decide WHEN it should be lit (currently only <see cref="OnHintTimerTick"/>/
    /// <see cref="UpdateHintTimerState"/> below) go through this, not HintBeacon.Visibility or
    /// HintBeacon.Opacity directly -- this is still the ONE place visibility (now: visibility AND
    /// its fade) flows through.
    ///
    /// <para>v1.4.1 Task A: lighting/extinguishing now animates a short opacity fade
    /// (<see cref="HintBeaconTiming.FadeDuration"/>) instead of an instant Visibility snap, and lit state starts
    /// a slow "breathing" loop (<see cref="StartBreathing"/>). <see cref="OnHintTimerTick"/> calls
    /// this every 250ms regardless of whether the state actually changed, so <see cref="_hintVisible"/>
    /// dedupes: a call that repeats the current state is a no-op, which is what keeps the breathing
    /// loop running smoothly instead of being restarted from scratch 4x/second.</para>
    ///
    /// <para>This does NOT give <see cref="SetHintEnabled"/>'s force-hide guarantee -- that path
    /// uses <see cref="ForceHideHintImmediately"/> instead, specifically to avoid leaving a fade (or
    /// the breathing loop) running past the moment the feature is disabled. See that method's doc
    /// comment.</para>
    /// </summary>
    public void SetHintVisible(bool visible)
    {
        if (visible == _hintVisible)
        {
            // Already in the requested state (including mid-fade toward it) -- nothing to do.
            // Without this guard, OnHintTimerTick's every-250ms call would restart the fade-in
            // storyboard (and therefore StartBreathing) on every single tick while the pointer sits
            // in the near-band, which would never let the breathing loop actually run.
            return;
        }

        _hintVisible = visible;

        if (visible)
        {
            HintBeacon.Visibility = Visibility.Visible;

            var fadeIn = new DoubleAnimation(HintBeacon.Opacity, HintBeaconTiming.OpacityLit, HintBeaconTiming.FadeDuration)
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut },
            };
            fadeIn.Completed += (_, _) =>
            {
                // Guard against a fast off/on/off flicker: only start breathing if we're still
                // supposed to be visible by the time this fade's Completed callback runs.
                if (_hintVisible)
                    StartBreathing();
            };
            HintBeacon.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        }
        else
        {
            // Read the CURRENT (possibly mid-breath) opacity before touching the animation, per
            // apple-design's "animate from the presentation value, not the target" -- fading out
            // from wherever the breathing loop happened to be, rather than snapping to
            // HintBeaconTiming.OpacityLit first, avoids a visible pop right as the fade-out begins.
            var current = HintBeacon.Opacity;
            StopBreathing();

            var fadeOut = new DoubleAnimation(current, 0.0, HintBeaconTiming.FadeDuration)
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseIn },
            };
            fadeOut.Completed += (_, _) =>
            {
                if (!_hintVisible)
                    HintBeacon.Visibility = Visibility.Collapsed;
            };
            HintBeacon.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }
    }

    /// <summary>Starts the slow breathing loop (v1.4.1 Task A) once the fade-in has landed at
    /// <see cref="HintBeaconTiming.OpacityLit"/>. Gentle by design: a single sine-eased AutoReverse animation
    /// between <see cref="HintBeaconTiming.OpacityLit"/> and <see cref="HintBeaconTiming.OpacityBreatheLow"/>, ~2s per full
    /// cycle, forever for as long as the beacon stays lit -- not the "aggressive strobing" the brief
    /// explicitly rules out.</summary>
    private void StartBreathing()
    {
        var breathe = new DoubleAnimation
        {
            From = HintBeaconTiming.OpacityLit,
            To = HintBeaconTiming.OpacityBreatheLow,
            Duration = HintBeaconTiming.BreathHalfCycle,
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        HintBeacon.BeginAnimation(UIElement.OpacityProperty, breathe);
    }

    /// <summary>Stops the breathing loop (if running) and pins Opacity's local value to whatever
    /// the animation's current effective value was, so a caller that immediately starts a new
    /// animation (the fade-out in <see cref="SetHintVisible"/>) reads a stable starting point rather
    /// than the DoubleAnimation removal reverting to the XAML default.</summary>
    private void StopBreathing()
    {
        var current = HintBeacon.Opacity;
        HintBeacon.BeginAnimation(UIElement.OpacityProperty, null);
        HintBeacon.Opacity = current;
    }

    /// <summary>
    /// The disable-path guarantee <see cref="SetHintEnabled"/>'s doc comment promises: a complete,
    /// synchronous, IMMEDIATE stop -- no fade, no breathing loop left running. Deliberately separate
    /// from <see cref="SetHintVisible"/>'s animated hide (used by the normal near/far poll
    /// transitions) because EdgeHintEnabled=false must never leave an orphaned animation clock on a
    /// collapsed element waiting to finish; BeginAnimation(..., null) removes the clock outright
    /// rather than letting a fade run its course.
    /// </summary>
    private void ForceHideHintImmediately()
    {
        _hintVisible = false;
        HintBeacon.BeginAnimation(UIElement.OpacityProperty, null);
        HintBeacon.Opacity = 0.0;
        HintBeacon.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Turns the proximity hint feature on/off (AppSettings.EdgeHintEnabled). While enabled AND
    /// this window is visible, <see cref="_hintTimer"/> polls the cursor every
    /// <see cref="HintPollInterval"/> and drives <see cref="SetHintVisible"/> via
    /// <see cref="ShelfPlacement.IsNearTriggerButOutside"/> -- lit only when the pointer is near
    /// the configured edge but at the wrong height to actually be in the hot zone. Disabling it
    /// (or the window going invisible -- see <see cref="UpdateHintTimerState"/>) stops the timer
    /// AND force-hides the beacon, so it can never be left lit with nothing polling to turn it
    /// back off.
    /// </summary>
    public void SetHintEnabled(bool enabled)
    {
        _hintFeatureEnabled = enabled;
        // UpdateHintTimerState already force-hides the beacon whenever the effective condition
        // (_hintFeatureEnabled && IsVisible) is false, which covers "just disabled" unconditionally
        // -- a second SetHintVisible(false) here would just repeat that same call.
        UpdateHintTimerState();
    }

    private void UpdateHintTimerState()
    {
        if (_hintFeatureEnabled && IsVisible)
        {
            _hintTimer.Start();
        }
        else
        {
            _hintTimer.Stop();
            ForceHideHintImmediately();
        }
    }

    private void OnHintTimerTick(object? sender, EventArgs e)
    {
        // _hintArea (and workArea/triggerRect alongside it) is only populated once Place() has
        // run at least once -- ApplySettings always calls it synchronously, and the timer itself
        // cannot start before ApplySettings has run (see UpdateHintTimerState/SetHintEnabled), so
        // this is just defensive, not the expected steady-state path.
        if (_settings is null || _hintArea is null)
        {
            return;
        }

        try
        {
            // Fresh cursor read against the CACHED geometry -- no MonitorGeometry.Resolve and no
            // ShelfPlacement.TriggerRect on this hot path; see Place()'s doc comment for why that
            // used to run 4x/second and doesn't need to.
            var (cursorX, cursorY) = MonitorGeometry.CursorDip(_hintArea.Value);

            var show = ShelfPlacement.IsNearTriggerButOutside(
                _hintWorkArea, _hintTriggerRect, _settings.Edge, cursorX, cursorY);

            SetHintVisible(show);
        }
        catch (Exception ex)
        {
            // A timer tick, not a user action -- degrade to "hint off" and keep polling rather
            // than let one bad cursor read kill the beacon (or the app) for the rest of the
            // session.
            FileLogger.Instance?.Warn(Module, $"proximity hint check failed: {ex.Message}");
            SetHintVisible(false);
        }
    }

    private void OnDpiChanged(object sender, System.Windows.DpiChangedEventArgs e)
    {
        if (_settings is not null)
            ApplySettings(_settings);
    }
}

/// <summary>
/// Pure animation-parameter class for <see cref="EdgeTriggerWindow"/>'s HintBeacon (v1.4.1 Task A).
/// The one place the fade duration, breathing half-cycle, and breathing opacity range are decided,
/// so a future edit cannot silently drift the beacon into the "aggressive strobing" (too-short fade,
/// too-fast breathing) or "unnoticeable" (near-zero breathing amplitude) territory the brief rules
/// out, without a test noticing -- the same role <see cref="IndicatorTiming"/> plays for
/// IndicatorWindow (v1.5: IndicatorBrightness, the pre-v1.5 analogue for color, was retired and
/// replaced by <see cref="TNDrop.Core.IndicatorPalette"/>). Public, not internal, for the same
/// reason: TNDrop.Tests references TNDrop.App as an ordinary project reference with no
/// InternalsVisibleTo.
/// </summary>
public static class HintBeaconTiming
{
    /// <summary>Duration of the beacon's fade in/out. Short and deliberate -- long enough to read
    /// as a transition rather than a snap, short enough that it never looks like it is lagging the
    /// 250ms poll that drives it.</summary>
    public static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(180);

    /// <summary>Half-cycle of the "breathing" loop while the beacon is lit: with
    /// <see cref="System.Windows.Media.Animation.DoubleAnimation.AutoReverse"/> this yields a full
    /// ~2s in-out cycle, matching the brief's "2s 周期程度" ask. Slow enough to read as a gentle,
    /// ambient lamp -- not a strobe -- and outside the near-0.2Hz/5s range apple-design flags as a
    /// reduced-motion concern.</summary>
    public static readonly TimeSpan BreathHalfCycle = TimeSpan.FromSeconds(1);

    /// <summary>Peak opacity while lit (also the fade-in target / fade-out start).</summary>
    public const double OpacityLit = 1.0;

    /// <summary>Trough opacity of the breathing loop. Amplitude (<see cref="OpacityLit"/> minus
    /// this, 0.28) is deliberately modest per the brief ("振幅は控えめ") -- the brightness boost
    /// that makes the beacon noticeable lives in HintBeaconMidStop's color/width (see
    /// EdgeTriggerWindow's constructor and XAML), not in a wide breathing swing.</summary>
    public const double OpacityBreatheLow = 0.72;
}
