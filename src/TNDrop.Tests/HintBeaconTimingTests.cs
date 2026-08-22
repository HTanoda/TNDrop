using TNDrop.UI;

namespace TNDrop.Tests;

/// <summary>
/// Pins <see cref="HintBeaconTiming"/>'s fade/breathing parameters (v1.4.1 Task A): the brief
/// explicitly rules out "aggressive strobing" and demands the breathing amplitude stay "控えめ"
/// (modest) while the beacon itself stays noticeable. A future edit that shrinks FadeDuration to
/// near-zero, speeds BreathHalfCycle into strobe territory, or widens/flattens the breathing
/// amplitude past what "gentle" or "noticeable" mean would land here first, the same role
/// IndicatorTimingTests plays for IndicatorWindow.
/// </summary>
public class HintBeaconTimingTests
{
    [Fact]
    public void FadeDuration_is_short_but_not_an_instant_snap()
    {
        // "ショートフェード" -- long enough to read as a transition, short enough it never looks
        // like it's lagging the 250ms poll that drives SetHintVisible.
        Assert.InRange(HintBeaconTiming.FadeDuration.TotalMilliseconds, 100, 300);
    }

    [Fact]
    public void Breathing_full_cycle_is_close_to_the_2s_the_brief_asks_for()
    {
        var fullCycle = HintBeaconTiming.BreathHalfCycle.TotalSeconds * 2; // AutoReverse doubles it
        Assert.InRange(fullCycle, 1.5, 3.0);
    }

    [Fact]
    public void Breathing_amplitude_is_modest_not_flat_and_not_a_hard_flicker()
    {
        var amplitude = HintBeaconTiming.OpacityLit - HintBeaconTiming.OpacityBreatheLow;

        // Modest ("振幅は控えめ"): noticeably breathing, but not swinging the whole way to
        // invisible (that would read as a flicker/strobe, which the brief explicitly rules out)
        // and not so shallow it may as well be static.
        Assert.InRange(amplitude, 0.1, 0.5);
        Assert.True(HintBeaconTiming.OpacityBreatheLow > 0.3, "breathing trough must stay clearly visible, not fade to near-zero");
    }

    [Fact]
    public void OpacityLit_is_the_fully_lit_ceiling()
    {
        Assert.Equal(1.0, HintBeaconTiming.OpacityLit);
    }
}
