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
}
