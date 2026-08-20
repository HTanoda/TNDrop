using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TNDrop.Core;
using TNDrop.Platform;
using TNDrop.Services;

namespace TNDrop.UI;

/// <summary>
/// The clipboard shelf: a panel that slides in from the configured screen edge and retracts a
/// moment after the pointer leaves. Content is a placeholder at this stage; only the geometry,
/// the slide animation and the retract timing are real.
/// </summary>
public partial class ShelfWindow : Window
{
    private const string Module = "ShelfWindow";

    /// <summary>Placement passes allowed per ApplySettings call. See the re-entrancy latch there.</summary>
    private const int MaxPlacementPasses = 2;

    private static readonly Duration SlideInDuration = new(TimeSpan.FromMilliseconds(250));
    private static readonly Duration SlideOutDuration = new(TimeSpan.FromMilliseconds(180));

    private readonly DispatcherTimer _retractTimer;

    private AppSettings? _settings;
    private EdgeSide _edge = EdgeSide.Left;
    private double _shownX;
    private double _hiddenX = -ShelfPlacement.ShelfWidth;
    private MonitorGeometry.WorkArea _area;
    private ShelfPlacement.Rect _rect;
    private bool _placed;
    private bool _pointerInside;
    private bool _slidingOut;
    private bool _applying;
    private bool _reapplyRequested;

    public ShelfWindow()
    {
        InitializeComponent();

        _retractTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _retractTimer.Tick += OnRetractTick;

        MouseEnter += OnPointerEnter;
        MouseLeave += OnPointerLeave;
        IsVisibleChanged += OnSelfVisibleChanged;
        DpiChanged += OnDpiChanged;

        // Create the HWND now. ApplySettings runs before the shelf is ever shown and its
        // device-pixel snap needs a handle; without this the first placement silently skips it.
        new WindowInteropHelper(this).EnsureHandle();
    }

    /// <summary>True while the pointer is over the shelf. Drives the retract timer.</summary>
    public bool IsPointerInside => _pointerInside || IsMouseOver;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Must run after the HWND exists. WS_EX_NOACTIVATE is what keeps the shelf from stealing
        // focus from whatever the user is typing in when it slides in.
        WindowStyles.MakeToolWindowNoActivate(this);
    }

    /// <summary>Recomputes geometry and retract timing from the settings, resolving monitor and DPI.</summary>
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
            FileLogger.Instance?.Error(Module, "Failed to place the shelf", ex);
        }
        finally
        {
            _reapplyRequested = false;
            _applying = false;
        }
    }

    private void Place(AppSettings s)
    {
        _edge = s.Edge;

        var area = MonitorGeometry.Resolve(s.MonitorDeviceName, this);
        var rect = ShelfPlacement.ShelfRect(new ShelfPlacement.Rect(area.X, area.Y, area.W, area.H), _edge);

        _area = area;
        _rect = rect;
        _placed = true;
        _shownX = rect.X;
        _hiddenX = ShelfPlacement.HiddenX(rect, _edge);

        Panel.CornerRadius = _edge == EdgeSide.Left
            ? new CornerRadius(0, 12, 12, 0)
            : new CornerRadius(12, 0, 0, 12);

        _retractTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(s.RetractDelayMs, 100, 10_000));

        // A settings change mid-slide-out finishes the retract rather than leaving the shelf
        // parked halfway with no timer running.
        var wasSlidingOut = _slidingOut;
        StopSlide();

        var showing = IsVisible && !wasSlidingOut;
        var x = showing ? _shownX : _hiddenX;

        Width = rect.W;
        Height = rect.H;
        Top = rect.Y;
        Left = x;

        if (!showing && IsVisible)
            Hide();

        SnapToDevicePixels(x);

        // StopSlide above killed any in-flight slide-in, so its Completed handler will not run to
        // arm the countdown. Without this the shelf would sit out with no timer.
        if (showing)
            ArmRetractIfPointerOutside();

        FileLogger.Instance?.Info(Module,
            $"placed on {area.DeviceName} scale {area.ScaleX:0.##}: shown X {_shownX:0}, " +
            $"hidden X {_hiddenX:0}, {rect.W:0}x{rect.H:0} DIP, retract {_retractTimer.Interval.TotalMilliseconds:0} ms");
    }

    /// <summary>Slides the shelf in from off-screen with a slight overshoot (250 ms, BackEase EaseOut).</summary>
    public void SlideIn()
    {
        // Read the live value first: if a retract is in flight this is the halfway position, and
        // reversing from there is what makes an interrupted retract feel continuous.
        var from = IsVisible ? Left : _hiddenX;

        StopSlide();
        _retractTimer.Stop();

        if (!IsVisible)
        {
            Left = _hiddenX;
            Show();
        }

        // Base value first, animation second: with FillBehavior.Stop the property falls back to
        // the base value the instant the clock ends, so the base value must already be the target.
        Left = _shownX;

        var animation = new DoubleAnimation(from, _shownX, SlideInDuration)
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 },
            FillBehavior = FillBehavior.Stop,
        };
        animation.Completed += OnSlideInCompleted;

        BeginAnimation(LeftProperty, animation);
    }

    private void OnSlideInCompleted(object? sender, EventArgs e)
    {
        if (_slidingOut || !IsVisible)
            return;

        // Land on the exact device pixel. Measured on a 125% display: without this the shelf
        // came to rest 2px short of the screen edge every time, leaving a sliver of desktop
        // showing down the side. FillBehavior.Stop reverts Left to a base value it already
        // equals, so the last animated frame -- which never lands exactly on the target -- is
        // what the HWND keeps. Snapping is cheap and makes the resting position exact.
        SnapToDevicePixels(_shownX);

        // A pointer that flicks past the trigger band and never lands on the shelf produces no
        // MouseEnter and therefore no MouseLeave, so nothing else would ever start the countdown
        // -- and the trigger band is hidden while the shelf is out, so the user would have no way
        // to dismiss it. Arm it here instead of relying on the pointer arriving.
        ArmRetractIfPointerOutside();
    }

    /// <summary>Pins the window to the device-pixel rectangle that <paramref name="xDip"/> maps to on the target monitor.</summary>
    private void SnapToDevicePixels(double xDip)
    {
        if (!_placed)
            return;

        MonitorGeometry.SnapToDeviceRect(this,
            xDip * _area.ScaleX, _rect.Y * _area.ScaleY,
            _rect.W * _area.ScaleX, _rect.H * _area.ScaleY);
    }

    /// <summary>Slides the shelf back off-screen (180 ms, QuadraticEase EaseIn) and hides it.</summary>
    public void SlideOut()
    {
        if (!IsVisible)
            return;

        var from = Left;

        StopSlide();
        _retractTimer.Stop();
        _slidingOut = true;

        Left = _hiddenX;

        var animation = new DoubleAnimation(from, _hiddenX, SlideOutDuration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop,
        };
        animation.Completed += OnSlideOutCompleted;

        BeginAnimation(LeftProperty, animation);
    }

    private void OnSlideOutCompleted(object? sender, EventArgs e)
    {
        // Cleared by a SlideIn that interrupted this retract; the shelf must stay visible.
        if (!_slidingOut)
            return;

        _slidingOut = false;
        BeginAnimation(LeftProperty, null);
        Left = _hiddenX;
        SnapToDevicePixels(_hiddenX);
        Hide();
    }

    private void StopSlide()
    {
        _slidingOut = false;
        BeginAnimation(LeftProperty, null);
    }

    private void OnPointerEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _pointerInside = true;
        _retractTimer.Stop();
    }

    private void OnPointerLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _pointerInside = false;
        ArmRetractIfPointerOutside();
    }

    private void OnSelfVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
            return;

        // A hidden window never gets a MouseLeave. Leaving the flag set would make
        // IsPointerInside permanently true and suppress every future retract -- the shelf would
        // open once more and then never close again.
        _pointerInside = false;
        _retractTimer.Stop();
    }

    /// <summary>
    /// Starts the retract countdown unless the pointer is on the shelf. Every path that leaves
    /// the shelf visible must call this: the shelf only ever hides itself on this timer, and the
    /// trigger band is hidden while the shelf is out, so a visible shelf with no timer running is
    /// a shelf the user cannot get rid of.
    /// </summary>
    private void ArmRetractIfPointerOutside()
    {
        _retractTimer.Stop();
        if (IsVisible && !IsPointerInside)
            _retractTimer.Start();
    }

    private void OnRetractTick(object? sender, EventArgs e)
    {
        if (IsPointerInside)
        {
            // Suppressed, not cancelled. Re-arm rather than drop the timer: if IsPointerInside is
            // wrong (or the MouseLeave that would normally re-arm never arrives), dropping it here
            // is what wedges the shelf open permanently.
            _retractTimer.Stop();
            _retractTimer.Start();
            return;
        }

        _retractTimer.Stop();
        SlideOut();
    }

    private void OnDpiChanged(object sender, System.Windows.DpiChangedEventArgs e)
    {
        if (_settings is not null)
            ApplySettings(_settings);
    }
}
