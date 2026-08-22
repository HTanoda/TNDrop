using TNDrop.Core;
using TNDrop.UI;

public class ShelfPlacementTests
{
    private static readonly ShelfPlacement.Rect Work = new(0, 0, 1920, 1040);

    [Fact]
    public void TriggerRect_left_center_40pct()
    {
        var r = ShelfPlacement.TriggerRect(Work, EdgeSide.Left, 3, 40, TriggerAlign.Center);
        Assert.Equal(0, r.X);
        Assert.Equal(3, r.W);
        Assert.Equal(416, r.H);            // 1040 * 0.40
        Assert.Equal(312, r.Y);            // (1040-416)/2
    }

    [Fact]
    public void TriggerRect_right_bottom()
    {
        var r = ShelfPlacement.TriggerRect(Work, EdgeSide.Right, 5, 25, TriggerAlign.Bottom);
        Assert.Equal(1915, r.X);           // 1920 - 5
        Assert.Equal(1040 - 260, r.Y);     // 下寄せ
    }

    [Fact]
    public void ShelfRect_and_hidden_x()
    {
        var s = ShelfPlacement.ShelfRect(Work, EdgeSide.Left);
        Assert.Equal(0, s.X); Assert.Equal(340, s.W);
        Assert.Equal(-340, ShelfPlacement.HiddenX(s, EdgeSide.Left));
        var s2 = ShelfPlacement.ShelfRect(Work, EdgeSide.Right);
        Assert.Equal(1920 - 340, s2.X);
        Assert.Equal(1920, ShelfPlacement.HiddenX(s2, EdgeSide.Right));
    }

    // ---- IsNearTriggerButOutside (v1.2 Task E proximity hint) ----------------------------------

    [Fact]
    public void IsNearTriggerButOutside_true_near_edge_but_wrong_height()
    {
        var trigger = ShelfPlacement.TriggerRect(Work, EdgeSide.Left, 3, 40, TriggerAlign.Center); // Y=312, H=416
        Assert.True(ShelfPlacement.IsNearTriggerButOutside(Work, trigger, EdgeSide.Left, cursorX: 5, cursorY: 100));
    }

    [Fact]
    public void IsNearTriggerButOutside_false_inside_the_real_hot_zone()
    {
        var trigger = ShelfPlacement.TriggerRect(Work, EdgeSide.Left, 3, 40, TriggerAlign.Center);
        Assert.False(ShelfPlacement.IsNearTriggerButOutside(Work, trigger, EdgeSide.Left, cursorX: 1, cursorY: 500));
    }

    [Fact]
    public void IsNearTriggerButOutside_false_far_from_the_edge()
    {
        var trigger = ShelfPlacement.TriggerRect(Work, EdgeSide.Left, 3, 40, TriggerAlign.Center);
        Assert.False(ShelfPlacement.IsNearTriggerButOutside(Work, trigger, EdgeSide.Left, cursorX: 50, cursorY: 100));
    }

    [Fact]
    public void IsNearTriggerButOutside_false_off_the_target_monitor_vertically()
    {
        var trigger = ShelfPlacement.TriggerRect(Work, EdgeSide.Left, 3, 40, TriggerAlign.Center);
        Assert.False(ShelfPlacement.IsNearTriggerButOutside(Work, trigger, EdgeSide.Left, cursorX: 5, cursorY: -10));
    }

    [Fact]
    public void IsNearTriggerButOutside_works_for_the_right_edge()
    {
        var trigger = ShelfPlacement.TriggerRect(Work, EdgeSide.Right, 3, 40, TriggerAlign.Center);
        Assert.True(ShelfPlacement.IsNearTriggerButOutside(Work, trigger, EdgeSide.Right, cursorX: 1918, cursorY: 50));
        Assert.False(ShelfPlacement.IsNearTriggerButOutside(Work, trigger, EdgeSide.Right, cursorX: 1800, cursorY: 50));
    }

    // Regression for fix round 1: the near-band width must come from triggerRect.W (the one place
    // TriggerRect's own clamp already lives), not from a second Math.Clamp(proximityPx, ...) that
    // could silently disagree with it. A wider band (proximityPx=10, so triggerRect.W=10) must
    // widen the near-band to match (10+24=34, above the HintMinBandDip floor so the floor doesn't
    // mask the tracking), not stay pinned to whatever a hardcoded/duplicated clamp would have
    // produced.
    [Fact]
    public void IsNearTriggerButOutside_near_band_tracks_the_actual_trigger_width()
    {
        var trigger = ShelfPlacement.TriggerRect(Work, EdgeSide.Left, 10, 40, TriggerAlign.Center);
        Assert.Equal(10, trigger.W);

        Assert.True(ShelfPlacement.IsNearTriggerButOutside(Work, trigger, EdgeSide.Left, cursorX: 34, cursorY: 100));  // within 34
        Assert.False(ShelfPlacement.IsNearTriggerButOutside(Work, trigger, EdgeSide.Left, cursorX: 35, cursorY: 100)); // past 34
    }

    // v1.4.1 Task A: HintProximityMarginPx widened 8->24 because the old near-band (sensitivity+8px,
    // often ~11px at typical settings) was too thin for users to ever trigger in practice -- a
    // cursor that used to land just OUTSIDE the band now lands INSIDE it.
    [Fact]
    public void IsNearTriggerButOutside_widened_margin_now_covers_what_the_old_8px_margin_missed()
    {
        var trigger = ShelfPlacement.TriggerRect(Work, EdgeSide.Left, 3, 40, TriggerAlign.Center); // W=3
        // Old formula (margin=8): nearWidth = 3 + 8 = 11, so cursorX=20 was OUTSIDE (false).
        // New formula (margin=24, floor=30): nearWidth = max(30, 3+24) = 30, cursorX=20 is INSIDE.
        Assert.True(ShelfPlacement.IsNearTriggerButOutside(Work, trigger, EdgeSide.Left, cursorX: 20, cursorY: 100));
    }

    // The new minimum-band floor: at a very thin trigger (proximityPx clamped to 1), the raw
    // formula (1+24=25) would still be too thin to reliably hit. HintMinBandDip forces a usable
    // floor regardless of how tight the user's sensitivity setting is.
    [Fact]
    public void IsNearTriggerButOutside_minimum_band_floor_applies_at_small_sensitivity()
    {
        var trigger = ShelfPlacement.TriggerRect(Work, EdgeSide.Left, 1, 40, TriggerAlign.Center);
        Assert.Equal(1, trigger.W);
        Assert.Equal(30, ShelfPlacement.HintMinBandDip);

        Assert.True(ShelfPlacement.IsNearTriggerButOutside(Work, trigger, EdgeSide.Left, cursorX: 30, cursorY: 100));  // within the floor
        Assert.False(ShelfPlacement.IsNearTriggerButOutside(Work, trigger, EdgeSide.Left, cursorX: 31, cursorY: 100)); // past the floor
    }

    // The vertical conditions (work-area bound, hot-zone exclusion) are untouched by the width
    // change -- pin that the existing vertical-rejection tests above still hold at the new margin
    // by re-running the hot-zone exclusion at a width where the floor does NOT apply, so this test
    // would fail if a future edit accidentally coupled the vertical check to nearWidth.
    [Fact]
    public void IsNearTriggerButOutside_vertical_hot_zone_exclusion_unchanged_by_wider_band()
    {
        var trigger = ShelfPlacement.TriggerRect(Work, EdgeSide.Left, 10, 40, TriggerAlign.Center); // Y=312, H=416, W=10 (no floor)
        // Within the new near-band horizontally (distFromEdge=5 < 34) but inside the real hot zone
        // vertically -- must still be false regardless of how wide the band got.
        Assert.False(ShelfPlacement.IsNearTriggerButOutside(Work, trigger, EdgeSide.Left, cursorX: 5, cursorY: 500));
    }
}
