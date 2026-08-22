using TNDrop.UI;

namespace TNDrop.Tests;

/// <summary>
/// Pins <see cref="IndicatorTiming"/>'s multiplier and scaling math (v1.3 Task E): the flash
/// duration boost applied to all 4 <c>IndicatorWindow</c> styles (Beacon/Bar/Pulse/Corner) comes
/// from this one shared constant, so a future edit cannot silently push it outside the design's
/// "~1.2x-1.5x" range, or let the styles' durations drift apart, without a test noticing.
/// </summary>
public class IndicatorTimingTests
{
    [Fact]
    public void DurationBoost_is_within_the_designed_1_2x_to_1_5x_range()
    {
        Assert.InRange(IndicatorTiming.DurationBoost, 1.2, 1.5);
    }

    [Theory]
    [InlineData(400, 520)] // Beacon
    [InlineData(300, 390)] // Bar
    [InlineData(450, 585)] // Pulse (per cycle -- IndicatorWindow plays this twice, see FlashPulse)
    [InlineData(350, 455)] // Corner
    public void Scale_multiplies_the_pre_v1_3_base_duration_by_DurationBoost(
        double baseMilliseconds, double expectedMilliseconds)
    {
        var scaled = IndicatorTiming.Scale(baseMilliseconds);

        Assert.Equal(expectedMilliseconds, scaled.TotalMilliseconds, precision: 6);
    }
}
