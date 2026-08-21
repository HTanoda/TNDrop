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
        Assert.True(ShelfPlacement.IsNearTriggerButOutside(Work, trigger, EdgeSide.Left, 3, cursorX: 5, cursorY: 100));
    }

    [Fact]
    public void IsNearTriggerButOutside_false_inside_the_real_hot_zone()
    {
        var trigger = ShelfPlacement.TriggerRect(Work, EdgeSide.Left, 3, 40, TriggerAlign.Center);
        Assert.False(ShelfPlacement.IsNearTriggerButOutside(Work, trigger, EdgeSide.Left, 3, cursorX: 1, cursorY: 500));
    }

    [Fact]
    public void IsNearTriggerButOutside_false_far_from_the_edge()
    {
        var trigger = ShelfPlacement.TriggerRect(Work, EdgeSide.Left, 3, 40, TriggerAlign.Center);
        Assert.False(ShelfPlacement.IsNearTriggerButOutside(Work, trigger, EdgeSide.Left, 3, cursorX: 50, cursorY: 100));
    }

    [Fact]
    public void IsNearTriggerButOutside_false_off_the_target_monitor_vertically()
    {
        var trigger = ShelfPlacement.TriggerRect(Work, EdgeSide.Left, 3, 40, TriggerAlign.Center);
        Assert.False(ShelfPlacement.IsNearTriggerButOutside(Work, trigger, EdgeSide.Left, 3, cursorX: 5, cursorY: -10));
    }

    [Fact]
    public void IsNearTriggerButOutside_works_for_the_right_edge()
    {
        var trigger = ShelfPlacement.TriggerRect(Work, EdgeSide.Right, 3, 40, TriggerAlign.Center);
        Assert.True(ShelfPlacement.IsNearTriggerButOutside(Work, trigger, EdgeSide.Right, 3, cursorX: 1918, cursorY: 50));
        Assert.False(ShelfPlacement.IsNearTriggerButOutside(Work, trigger, EdgeSide.Right, 3, cursorX: 1800, cursorY: 50));
    }
}
