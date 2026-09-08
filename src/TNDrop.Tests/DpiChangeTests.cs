using TNDrop.UI;

/// <summary>
/// v1.8.3: whether a WM_DPICHANGED notification carries a real change. The production laptop
/// (dock, 4K + 3K monitors) delivered 466 notifications in one day, every one of them 120->120,
/// 461 of them while the shelf sat hidden off-screen -- Windows re-evaluating an off-screen
/// window's monitor association and re-announcing the same DPI. Re-placing on those was harmless
/// but pointless, and it flooded the log.
/// </summary>
public class DpiChangeTests
{
    [Fact]
    public void Same_dpi_on_both_axes_is_not_a_change()
    {
        Assert.False(ShelfPlacement.DpiActuallyChanged(120, 120, 120, 120));
    }

    [Theory]
    [InlineData(120, 120, 144, 144)]
    [InlineData(96, 96, 120, 120)]
    [InlineData(120, 120, 120, 144)]
    [InlineData(120, 120, 144, 120)]
    public void Any_axis_differing_is_a_change(double oldX, double oldY, double newX, double newY)
    {
        Assert.True(ShelfPlacement.DpiActuallyChanged(oldX, oldY, newX, newY));
    }

    [Fact]
    public void Sub_pixel_noise_below_half_a_dpi_is_not_a_change()
    {
        // Values arrive as doubles (PixelsPerInchX); a rounding wobble must not count.
        Assert.False(ShelfPlacement.DpiActuallyChanged(120, 120, 120.0001, 119.9999));
    }
}
