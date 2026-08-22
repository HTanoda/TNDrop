using TNDrop.UI;

namespace TNDrop.Tests;

/// <summary>
/// Pins <see cref="IndicatorBrightness"/>'s math (v1.3 Task E, review round 1). Round 1's fix used
/// one shared, fully-opaque color for all 4 <c>IndicatorWindow</c> flash styles, which measured out
/// to a +43% alpha-composited-luminance gain for Beacon/Bar/Pulse (baseline alpha 0xCC) but only
/// +27% for Corner (baseline alpha 0xE6, less headroom before clipping to opaque) -- these tests
/// pin both the exact brightened RGB bytes <see cref="IndicatorBrightness.Brighten"/> computes AND
/// the resulting proportional gain, so a future edit cannot silently let the two styles' gains
/// drift back apart without a test noticing.
/// </summary>
public class IndicatorBrightnessTests
{
    [Fact]
    public void TargetGain_is_within_the_30_to_40_percent_band_review_round_1_asked_for()
    {
        Assert.InRange(IndicatorBrightness.TargetGain, 1.30, 1.40);
    }

    [Fact]
    public void Brighten_computes_the_exact_shared_color_for_Beacon_Bar_Pulse()
    {
        var (r, g, b) = IndicatorBrightness.Brighten(
            IndicatorBrightness.BaseAlphaShared,
            IndicatorBrightness.BaseR, IndicatorBrightness.BaseG, IndicatorBrightness.BaseB);

        Assert.Equal(0x76, r);
        Assert.Equal(0xD1, g);
        Assert.Equal(0xFB, b);
    }

    [Fact]
    public void Brighten_computes_the_exact_color_for_Corner()
    {
        var (r, g, b) = IndicatorBrightness.Brighten(
            IndicatorBrightness.BaseAlphaCorner,
            IndicatorBrightness.BaseR, IndicatorBrightness.BaseG, IndicatorBrightness.BaseB);

        Assert.Equal(0xA6, r);
        Assert.Equal(0xE1, g);
        Assert.Equal(0xFC, b);
    }

    [Theory]
    [InlineData(0xCC)] // Beacon/Bar/Pulse baseline alpha
    [InlineData(0xE6)] // Corner baseline alpha
    public void Brighten_lands_within_the_target_gain_band_regardless_of_baseline_alpha(byte baseAlpha)
    {
        var baseEffective = IndicatorBrightness.EffectiveLuminance(
            baseAlpha, IndicatorBrightness.BaseR, IndicatorBrightness.BaseG, IndicatorBrightness.BaseB);

        var (r, g, b) = IndicatorBrightness.Brighten(
            baseAlpha, IndicatorBrightness.BaseR, IndicatorBrightness.BaseG, IndicatorBrightness.BaseB);
        // Brighten always returns a fully-opaque color, so its effective luminance IS its plain
        // luminance (alpha=255/255=1.0) -- this is the exact quantity review round 1's own audit
        // measured brightness by.
        var brightenedEffective = IndicatorBrightness.EffectiveLuminance(255, r, g, b);

        var gain = brightenedEffective / baseEffective;

        // 30%-40% band (review round 1's requested range), with a small tolerance for the byte
        // rounding Brighten does when converting its analytic blend fraction to an actual RGB.
        Assert.InRange(gain, 1.29, 1.41);
    }

    [Fact]
    public void Beacon_Bar_Pulse_and_Corner_gains_land_within_one_point_of_each_other()
    {
        var sharedBaseEffective = IndicatorBrightness.EffectiveLuminance(
            IndicatorBrightness.BaseAlphaShared,
            IndicatorBrightness.BaseR, IndicatorBrightness.BaseG, IndicatorBrightness.BaseB);
        var (sharedR, sharedG, sharedB) = IndicatorBrightness.Brighten(
            IndicatorBrightness.BaseAlphaShared,
            IndicatorBrightness.BaseR, IndicatorBrightness.BaseG, IndicatorBrightness.BaseB);
        var sharedGain = IndicatorBrightness.EffectiveLuminance(255, sharedR, sharedG, sharedB) / sharedBaseEffective;

        var cornerBaseEffective = IndicatorBrightness.EffectiveLuminance(
            IndicatorBrightness.BaseAlphaCorner,
            IndicatorBrightness.BaseR, IndicatorBrightness.BaseG, IndicatorBrightness.BaseB);
        var (cornerR, cornerG, cornerB) = IndicatorBrightness.Brighten(
            IndicatorBrightness.BaseAlphaCorner,
            IndicatorBrightness.BaseR, IndicatorBrightness.BaseG, IndicatorBrightness.BaseB);
        var cornerGain = IndicatorBrightness.EffectiveLuminance(255, cornerR, cornerG, cornerB) / cornerBaseEffective;

        // The whole point of review round 1's fix: Beacon/Bar/Pulse and Corner must land at
        // (approximately) the SAME proportional gain, not the same absolute color. Before the fix
        // these differed by 16 points (1.43 vs 1.27); after, they differ by well under 1.
        Assert.InRange(System.Math.Abs(sharedGain - cornerGain), 0.0, 0.01);
    }
}
