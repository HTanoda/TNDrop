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

    public IndicatorWindow()
    {
        InitializeComponent();
        ApplyBrightenedColors();

        // Create the HWND now so MakeClickThrough (called from OnSourceInitialized) and the
        // first Place() have a handle to work with, matching EdgeTriggerWindow/ShelfWindow.
        new WindowInteropHelper(this).EnsureHandle();
    }

    /// <summary>
    /// v1.3 Task E (review round 1): overwrites the XAML placeholder colors (already the correct
    /// computed values, kept in sync as a fallback -- see IndicatorWindow.xaml's own comment) with
    /// <see cref="IndicatorBrightness.Brighten"/>'s live output, so the ACTUAL running colors are
    /// traceable to one function call per style rather than to hex literals that could silently
    /// drift from it. Beacon/Bar/Pulse share one color (their baseline alpha, 0xCC, is the same);
    /// Corner gets its own (baseline alpha 0xE6 had less headroom, so it needs a different RGB to
    /// land at the SAME proportional gain -- see IndicatorBrightness's doc comment for the
    /// review-round-1 story). Named elements (not a shared resource) are used specifically because
    /// XAML's StaticResource is resolved once at parse time -- writing a new value into
    /// Resources[...] afterward would not propagate to brushes already built from it.
    /// </summary>
    private void ApplyBrightenedColors()
    {
        var (sharedR, sharedG, sharedB) = IndicatorBrightness.Brighten(
            IndicatorBrightness.BaseAlphaShared, IndicatorBrightness.BaseR, IndicatorBrightness.BaseG, IndicatorBrightness.BaseB);
        var shared = System.Windows.Media.Color.FromArgb(255, sharedR, sharedG, sharedB);
        var sharedTransparent = System.Windows.Media.Color.FromArgb(0, sharedR, sharedG, sharedB);

        BeaconGradientStart.Color = sharedTransparent;
        BeaconGradientMid.Color = shared;
        BeaconGradientEnd.Color = sharedTransparent;
        BarBrush.Color = shared;
        PulseBrush.Color = shared;

        var (cornerR, cornerG, cornerB) = IndicatorBrightness.Brighten(
            IndicatorBrightness.BaseAlphaCorner, IndicatorBrightness.BaseR, IndicatorBrightness.BaseG, IndicatorBrightness.BaseB);
        CornerBrush.Color = System.Windows.Media.Color.FromArgb(255, cornerR, cornerG, cornerB);
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

        // v1.3 Task E: "パルスは1周追加" -- one extra cycle. From/To animations restart from the
        // From value on every repeat, so RepeatBehavior(2) plays two full grow-and-fade rings back
        // to back with no extra wiring; Completed still fires exactly once, after both iterations,
        // which is what resets PulseCircle/PulseScale back to their rest values below. Each cycle
        // already runs at the boosted PulseDuration (see the field above), so two of them read as a
        // slow double-ping, not a flicker -- the "no aggressive strobing" constraint stays intact
        // because nothing here is a fast repeated flash, just one longer, calmer confirmation.
        var repeatTwice = new RepeatBehavior(2);
        fade.RepeatBehavior = repeatTwice;
        grow.RepeatBehavior = repeatTwice;

        PulseCircle.BeginAnimation(OpacityProperty, fade);
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

/// <summary>
/// Pure brightness-boost math for <see cref="IndicatorWindow"/>'s 4 flash colors (v1.3 Task E,
/// review round 1). The original fix used ONE shared, fully-opaque color for all 4 styles, which
/// looked uniform by construction but measured out wrong: Beacon/Bar/Pulse's pre-v1.3 baseline
/// alpha (<see cref="BaseAlphaShared"/>, 0xCC) had much more headroom before clipping to fully
/// opaque than Corner's (<see cref="BaseAlphaCorner"/>, 0xE6), so the SAME final color gave
/// Beacon/Bar/Pulse a measured +43% alpha-composited-luminance gain and Corner only +27% (review
/// round 1's own audit; see task-E-report.md's fix-report section for the exact before/after
/// numbers). This class is the single place "how much brighter" is computed FROM a target
/// PROPORTIONAL gain (<see cref="TargetGain"/>) applied to each style's OWN baseline luminance, so
/// styles that started brighter still land at the same proportional gain instead of the same
/// absolute color -- the "one resolution, parameterized" fix review round 1 asked for, instead of
/// two independently hand-tuned hex literals that could drift apart on a future edit. Pure and
/// static so it is directly testable (see IndicatorBrightnessTests) without touching WPF.
/// </summary>
public static class IndicatorBrightness
{
    /// <summary>
    /// Target proportional gain in perceptual, alpha-composited luminance over black (ITU-R BT.601
    /// weights, matching review round 1's own audit formula): 1.35 = +35%, the midpoint of the
    /// 30%-40% band review round 1 asked for after finding the original fix's uniform-color
    /// approach landed at +43%/+27% instead. Applied identically to every style's OWN baseline
    /// effective luminance (see <see cref="Brighten"/>), never to a shared baseline.
    /// </summary>
    public const double TargetGain = 1.35;

    // Pre-v1.3 base color, shared hue across all 4 styles: RGB 0x5AC8FA, alpha 0xCC for
    // Beacon/Bar/Pulse, 0xE6 for Corner (Corner was always slightly more opaque than the other 3).
    public const byte BaseR = 0x5A;
    public const byte BaseG = 0xC8;
    public const byte BaseB = 0xFA;
    public const byte BaseAlphaShared = 0xCC;
    public const byte BaseAlphaCorner = 0xE6;

    /// <summary>ITU-R BT.601 perceptual luminance of an opaque RGB triple (each channel 0-255).</summary>
    public static double Luminance(byte r, byte g, byte b) =>
        (0.299 * r) + (0.587 * g) + (0.114 * b);

    /// <summary>Luminance composited over black by <paramref name="a"/> (0-255) -- what a viewer
    /// actually perceives for a translucent color on a dark background, which is both what this
    /// class targets and what review round 1's own audit measured.</summary>
    public static double EffectiveLuminance(byte a, byte r, byte g, byte b) =>
        (a / 255.0) * Luminance(r, g, b);

    /// <summary>
    /// Returns a fully-opaque (alpha=255) color that lightens (<paramref name="baseR"/>,
    /// <paramref name="baseG"/>, <paramref name="baseB"/>) toward white by just enough that its
    /// (now alpha=255) luminance equals <paramref name="baseA"/>'s baseline effective luminance
    /// times <paramref name="gain"/>. Solves the white-blend fraction analytically -- rather than
    /// hand-tuning RGB by eye -- and clamps it to [0,1] so a pathological gain can never overshoot
    /// pure white or undershoot the original (unbrightened) color.
    /// </summary>
    public static (byte R, byte G, byte B) Brighten(
        byte baseA, byte baseR, byte baseG, byte baseB, double gain = TargetGain)
    {
        var baseLuminance = Luminance(baseR, baseG, baseB);
        var baseEffective = (baseA / 255.0) * baseLuminance;
        var targetEffective = baseEffective * gain;

        const double whiteLuminance = 255.0;
        var blend = (targetEffective - baseLuminance) / (whiteLuminance - baseLuminance);
        blend = Math.Clamp(blend, 0.0, 1.0);

        byte Lerp(byte from) =>
            (byte)Math.Round(from + ((255 - from) * blend), MidpointRounding.AwayFromZero);

        return (Lerp(baseR), Lerp(baseG), Lerp(baseB));
    }
}
