using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using TNDrop.Core;
using TNDrop.Platform;
using TNDrop.Services;

namespace TNDrop.UI;

/// <summary>
/// A click-through, never-activating overlay that flashes a brief light at the configured screen
/// edge to confirm a clipboard capture. One instance lives for the app's whole lifetime; every
/// <see cref="Flash"/> call re-resolves the target monitor (in case the user changed it) and
/// restarts whichever visual matches the current <see cref="TNDrop.Core.IndicatorStyle"/>,
/// interrupting any flash already in progress.
/// </summary>
public partial class IndicatorWindow : Window
{
    private const string Module = "IndicatorWindow";

    private static readonly Duration BeaconDuration = new(TimeSpan.FromMilliseconds(400));
    private static readonly Duration BarDuration = new(TimeSpan.FromMilliseconds(300));
    private static readonly Duration PulseDuration = new(TimeSpan.FromMilliseconds(450));
    private static readonly Duration CornerDuration = new(TimeSpan.FromMilliseconds(350));

    public IndicatorWindow()
    {
        InitializeComponent();

        // Create the HWND now so MakeClickThrough (called from OnSourceInitialized) and the
        // first Place() have a handle to work with, matching EdgeTriggerWindow/ShelfWindow.
        new WindowInteropHelper(this).EnsureHandle();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Must run after the HWND exists. This is what actually makes the window transparent to
        // clicks and keyboard focus -- AllowsTransparency alone only controls pixel alpha.
        WindowStyles.MakeClickThrough(this);
    }

    /// <summary>
    /// Places the window on the configured monitor, then plays the flash for <paramref name="style"/>
    /// at <paramref name="edge"/>. Safe to call repeatedly in quick succession: each call restarts
    /// its animation from the top rather than queuing.
    /// </summary>
    public void Flash(IndicatorStyle style, EdgeSide edge)
    {
        try
        {
            Place(edge);

            switch (style)
            {
                case IndicatorStyle.Beacon:
                    FlashElement(BeaconLight, edge, BeaconDuration);
                    break;
                case IndicatorStyle.Bar:
                    FlashElement(BarLight, edge, BarDuration);
                    break;
                case IndicatorStyle.Pulse:
                    FlashPulse(edge);
                    break;
                case IndicatorStyle.Corner:
                    FlashElement(CornerDot, edge, CornerDuration);
                    break;
            }
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Warn(Module, $"Flash failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves the configured monitor's work area and pins this window flush against
    /// <paramref name="edge"/>, spanning its full height. Reads
    /// <see cref="TNDrop.App.Settings"/> directly for the monitor choice (mirrors
    /// ShelfWindow's parameterless constructor reading <c>TNDrop.App.Store</c> -- both are
    /// composed by <c>App.OnStartup</c>, not by a caller-supplied parameter), guarded for
    /// design-time/test safety.
    /// </summary>
    private void Place(EdgeSide edge)
    {
        var monitorDeviceName = global::TNDrop.App.Settings?.MonitorDeviceName;
        var area = MonitorGeometry.Resolve(monitorDeviceName, this);

        Width = 64;
        Height = area.H;
        Top = area.Y;
        Left = edge == EdgeSide.Left ? area.X : area.X + area.W - Width;

        MonitorGeometry.SnapToDeviceRect(this,
            Left * area.ScaleX, area.Y * area.ScaleY,
            Width * area.ScaleX, area.H * area.ScaleY);
    }

    private void FlashElement(FrameworkElement element, EdgeSide edge, Duration duration)
    {
        element.HorizontalAlignment = edge == EdgeSide.Left
            ? System.Windows.HorizontalAlignment.Left
            : System.Windows.HorizontalAlignment.Right;

        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = 1.0;

        var fade = new DoubleAnimation(1.0, 0.0, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop,
        };
        fade.Completed += (_, _) => element.Opacity = 0.0;

        element.BeginAnimation(OpacityProperty, fade);
    }

    private void FlashPulse(EdgeSide edge)
    {
        PulseCircle.HorizontalAlignment = edge == EdgeSide.Left
            ? System.Windows.HorizontalAlignment.Left
            : System.Windows.HorizontalAlignment.Right;

        PulseCircle.BeginAnimation(OpacityProperty, null);
        PulseScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        PulseScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);

        PulseCircle.Opacity = 1.0;
        PulseScale.ScaleX = 1.0;
        PulseScale.ScaleY = 1.0;

        var fade = new DoubleAnimation(1.0, 0.0, PulseDuration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop,
        };
        fade.Completed += (_, _) => PulseCircle.Opacity = 0.0;

        var grow = new DoubleAnimation(1.0, 6.0, PulseDuration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        grow.Completed += (_, _) =>
        {
            PulseScale.ScaleX = 1.0;
            PulseScale.ScaleY = 1.0;
        };

        PulseCircle.BeginAnimation(OpacityProperty, fade);
        PulseScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, grow);
        PulseScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, grow);
    }
}
