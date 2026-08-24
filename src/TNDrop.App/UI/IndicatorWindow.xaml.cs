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

    // v1.3 Task E: each style's flash duration is the pre-v1.3 base (comment) scaled by the SAME
    // IndicatorTiming.DurationBoost, so "make the flash more noticeable" moves all 4 styles
    // together from one constant instead of four hand-edited numbers that could drift apart over
    // time (see IndicatorTiming's own doc comment). Bases: Beacon 400ms, Bar 300ms, Pulse 450ms
    // (per cycle - see FlashPulse for the extra cycle), Corner 350ms.
    private static readonly Duration BeaconDuration = new(IndicatorTiming.Scale(400));
    private static readonly Duration BarDuration = new(IndicatorTiming.Scale(300));
    private static readonly Duration PulseDuration = new(IndicatorTiming.Scale(450));
    private static readonly Duration CornerDuration = new(IndicatorTiming.Scale(350));

    // v1.5: flash's peak opacity (Settings.IndicatorOpacityPercent / 100).
    // FlashElement / FlashPulse use this instead of 1.0. Updated by ApplyPalette.
    private double _peakOpacity = 1.0;

    public IndicatorWindow()
    {
        InitializeComponent();
        ApplyPalette();

        // Create the HWND now so MakeClickThrough (called from OnSourceInitialized) and the
        // first Place() have a handle to work with, matching EdgeTriggerWindow/ShelfWindow.
        new WindowInteropHelper(this).EnsureHandle();
    }

    /// <summary>
    /// Re-reads Settings (IndicatorColor / IndicatorOpacityPercent) and updates every style's
    /// brushes and the peak opacity (v1.5). Colors are handed out from ONE
    /// IndicatorPalette.Resolve call to every brush -- the XAML placeholder colors are a
    /// design-time fallback only. Called from the constructor, and from
    /// App.SetIndicatorColor / SetIndicatorOpacityPercent when the setting changes (applies
    /// immediately). Settings null at design-time/test-time falls back to the default color.
    /// </summary>
    public void ApplyPalette()
    {
        var settings = global::TNDrop.App.Settings;
        if (!IndicatorPalette.TryParseHex(settings?.IndicatorColor, out var baseColor))
        {
            IndicatorPalette.TryParseHex(IndicatorPalette.DefaultColorHex, out baseColor);
        }

        var (fill, outline, _) = IndicatorPalette.Resolve(baseColor.R, baseColor.G, baseColor.B);
        var fillColor = System.Windows.Media.Color.FromArgb(255, fill.R, fill.G, fill.B);
        var fillTransparent = System.Windows.Media.Color.FromArgb(0, fill.R, fill.G, fill.B);
        var outlineColor = System.Windows.Media.Color.FromArgb(255, outline.R, outline.G, outline.B);
        var outlineTransparent = System.Windows.Media.Color.FromArgb(0, outline.R, outline.G, outline.B);

        BeaconGradientStart.Color = fillTransparent;
        BeaconGradientMid.Color = fillColor;
        BeaconGradientEnd.Color = fillTransparent;
        BeaconOutlineStart.Color = outlineTransparent;
        BeaconOutlineMid.Color = outlineColor;
        BeaconOutlineEnd.Color = outlineTransparent;
        BarBrush.Color = fillColor;
        BarOutlineBrush.Color = outlineColor;
        PulseBrush.Color = fillColor;
        PulseStrokeBrush.Color = outlineColor;
        CornerBrush.Color = fillColor;
        CornerStrokeBrush.Color = outlineColor;

        _peakOpacity = (settings?.IndicatorOpacityPercent ?? 100) / 100.0;
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
                    FlashElement(BeaconGroup, edge, BeaconDuration);
                    break;
                case IndicatorStyle.Bar:
                    FlashElement(BarGroup, edge, BarDuration);
                    break;
                case IndicatorStyle.Pulse:
                    FlashPulse(edge);
                    break;
                case IndicatorStyle.Corner:
                    FlashElement(CornerGroup, edge, CornerDuration);
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
        element.Opacity = _peakOpacity;

        var fade = new DoubleAnimation(_peakOpacity, 0.0, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop,
        };
        fade.Completed += (_, _) => element.Opacity = 0.0;

        element.BeginAnimation(OpacityProperty, fade);
    }

    private void FlashPulse(EdgeSide edge)
    {
        PulseGroup.HorizontalAlignment = edge == EdgeSide.Left
            ? System.Windows.HorizontalAlignment.Left
            : System.Windows.HorizontalAlignment.Right;

        PulseGroup.BeginAnimation(OpacityProperty, null);
        PulseScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        PulseScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);

        PulseGroup.Opacity = _peakOpacity;
        PulseScale.ScaleX = 1.0;
        PulseScale.ScaleY = 1.0;

        var fade = new DoubleAnimation(_peakOpacity, 0.0, PulseDuration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop,
        };
        fade.Completed += (_, _) => PulseGroup.Opacity = 0.0;

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

        // v1.3 Task E: "パルスは1周追加" -- one extra cycle. From/To animations restart from the
        // From value on every repeat, so RepeatBehavior(2) plays two full grow-and-fade rings back
        // to back with no extra wiring; Completed still fires exactly once, after both iterations,
        // which is what resets PulseGroup/PulseScale back to their rest values below. Each cycle
        // already runs at the boosted PulseDuration (see the field above), so two of them read as a
        // slow double-ping, not a flicker -- the "no aggressive strobing" constraint stays intact
        // because nothing here is a fast repeated flash, just one longer, calmer confirmation.
        var repeatTwice = new RepeatBehavior(2);
        fade.RepeatBehavior = repeatTwice;
        grow.RepeatBehavior = repeatTwice;

        PulseGroup.BeginAnimation(OpacityProperty, fade);
        PulseScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, grow);
        PulseScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, grow);
    }
}

/// <summary>
/// Pure duration-scaling helper for <see cref="IndicatorWindow"/> (v1.3 Task E). The single place
/// that turns a style's pre-v1.3 base duration into its boosted one, so
/// Beacon/Bar/Pulse/CornerDuration all move together under one shared multiplier instead of four
/// independently-tuned numbers that could drift outside the "~1.2-1.5x" design range one at a time
/// (see the one-resolution-per-related-fields guidance this mirrors). Pure and static so it is
/// testable without touching WPF (see IndicatorTimingTests). Public (not internal) because
/// TNDrop.Tests references TNDrop.App as an ordinary project reference with no
/// InternalsVisibleTo -- the project's other pure-logic helpers (e.g. TextScaleMap) follow the
/// same convention.
/// </summary>
public static class IndicatorTiming
{
    /// <summary>Shared duration multiplier: ~1.3x, inside the v1.3 design range of 1.2x-1.5x.</summary>
    public const double DurationBoost = 1.3;

    public static TimeSpan Scale(double baseMilliseconds) =>
        TimeSpan.FromMilliseconds(baseMilliseconds * DurationBoost);
}
