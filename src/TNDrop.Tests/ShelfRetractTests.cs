using TNDrop.UI;

/// <summary>
/// The retract-arming rule (v1.2 Task B). Regression coverage for a defect the Task B probe caught
/// on its first run: a shelf opened by an OLE drag over the trigger band armed NO countdown at all,
/// because the drag-open grace made the composite IsPointerInside true and the old rule was a plain
/// "arm only when nothing is holding the shelf". Nothing was then left alive to notice the grace
/// expiring, so the shelf stayed on screen -- and the trigger band is hidden while the shelf is
/// out, so the user had no way to dismiss it.
/// </summary>
public class ShelfRetractTests
{
    // ---- the defect scenario, spelled out -----------------------------------------------------

    [Fact]
    public void A_drag_opened_shelf_always_ends_up_with_a_live_timer()
    {
        // Exactly the state right after ShelfWindow.SlideInForDrag: visible, the drag has not
        // reached the shelf yet (so no MouseEnter and no DragEnter has happened), and the ONLY
        // thing holding it open is the self-expiring grace -- which is also why IsPointerInside is
        // true. This must still arm, or nothing will ever retract the shelf.
        Assert.True(ShelfRetract.ShouldArm(isVisible: true, pointerInside: true, dragOpenGraceActive: true));
    }

    [Fact]
    public void The_grace_expiring_leaves_the_shelf_armed_the_ordinary_way()
    {
        // The next tick after the deadline passes: the grace term drops out of IsPointerInside and
        // the plain rule takes over. Same answer, reached by the other branch -- which is what makes
        // the retract actually happen on that tick.
        Assert.True(ShelfRetract.ShouldArm(isVisible: true, pointerInside: false, dragOpenGraceActive: false));
    }

    // ---- the ordinary rule is unchanged -------------------------------------------------------

    [Fact]
    public void A_pointer_on_the_shelf_suppresses_the_countdown()
    {
        Assert.False(ShelfRetract.ShouldArm(isVisible: true, pointerInside: true, dragOpenGraceActive: false));
    }

    [Fact]
    public void Nothing_holding_a_visible_shelf_arms_it()
    {
        Assert.True(ShelfRetract.ShouldArm(isVisible: true, pointerInside: false, dragOpenGraceActive: false));
    }

    [Fact]
    public void A_grace_that_is_still_running_with_the_pointer_away_arms_too()
    {
        // Reachable in practice: OnShelfDragLeave re-arms while a grace from an earlier
        // SlideInForDrag has not been cleared yet. Arming is right either way -- the tick handler
        // re-evaluates.
        Assert.True(ShelfRetract.ShouldArm(isVisible: true, pointerInside: false, dragOpenGraceActive: true));
    }

    // ---- a hidden shelf never arms ------------------------------------------------------------

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void A_hidden_shelf_never_arms(bool pointerInside, bool dragOpenGraceActive)
    {
        // Including with the grace still running: OnSelfVisibleChanged clears it, but a timer armed
        // against a hidden shelf would fire into the NEXT appearance rather than this one.
        Assert.False(ShelfRetract.ShouldArm(isVisible: false, pointerInside, dragOpenGraceActive));
    }
}
