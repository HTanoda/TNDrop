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
        Assert.True(ShelfRetract.ShouldArm(isVisible: true, pinned: false, pointerInside: true, dragOpenGraceActive: true));
    }

    [Fact]
    public void The_grace_expiring_leaves_the_shelf_armed_the_ordinary_way()
    {
        // The next tick after the deadline passes: the grace term drops out of IsPointerInside and
        // the plain rule takes over. Same answer, reached by the other branch -- which is what makes
        // the retract actually happen on that tick.
        Assert.True(ShelfRetract.ShouldArm(isVisible: true, pinned: false, pointerInside: false, dragOpenGraceActive: false));
    }

    // ---- the ordinary rule is unchanged -------------------------------------------------------

    [Fact]
    public void A_pointer_on_the_shelf_suppresses_the_countdown()
    {
        Assert.False(ShelfRetract.ShouldArm(isVisible: true, pinned: false, pointerInside: true, dragOpenGraceActive: false));
    }

    [Fact]
    public void Nothing_holding_a_visible_shelf_arms_it()
    {
        Assert.True(ShelfRetract.ShouldArm(isVisible: true, pinned: false, pointerInside: false, dragOpenGraceActive: false));
    }

    [Fact]
    public void A_grace_that_is_still_running_with_the_pointer_away_arms_too()
    {
        // Reachable in practice: OnShelfDragLeave re-arms while a grace from an earlier
        // SlideInForDrag has not been cleared yet. Arming is right either way -- the tick handler
        // re-evaluates.
        Assert.True(ShelfRetract.ShouldArm(isVisible: true, pinned: false, pointerInside: false, dragOpenGraceActive: true));
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
        Assert.False(ShelfRetract.ShouldArm(isVisible: false, pinned: false, pointerInside, dragOpenGraceActive));
    }

    // ---- v1.5: ピン止めは自動格納を一切起動しない --------------------------------------------

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void A_pinned_shelf_never_arms(bool pointerInside, bool dragOpenGraceActive)
    {
        // ピンは「自動格納の抑止」そのもの。pointer/grace のどの組合せでもタイマーを
        // 起動しない。明示的な閉じる (×) は ShouldArm を通らないので影響を受けない。
        Assert.False(ShelfRetract.ShouldArm(
            isVisible: true, pinned: true, pointerInside, dragOpenGraceActive));
    }

    [Fact]
    public void A_hidden_shelf_never_arms_even_when_pinned()
    {
        Assert.False(ShelfRetract.ShouldArm(
            isVisible: false, pinned: true, pointerInside: false, dragOpenGraceActive: false));
    }

    // --- v1.7.1 CursorHolds: 静止カーソルの物理チェック (設計書 §3) ---
    // shelf = (0, 30, 340, 540)、trigger = (0, 200, 3, 240) : 左端・トリガーはシェルフに包含
    // trigger2 = (0, 0, 3, 100) : シェルフ上端より上にはみ出すトリガー (Top align + 大ホットゾーン相当)

    private static readonly ShelfPlacement.Rect Shelf = new(0, 30, 340, 540);
    private static readonly ShelfPlacement.Rect TriggerInside = new(0, 200, 3, 240);
    private static readonly ShelfPlacement.Rect TriggerSticksOut = new(0, 0, 3, 100);

    [Theory]
    [InlineData(170, 300)]  // シェルフ中央
    [InlineData(1, 210)]    // トリガー帯内 (シェルフにも含まれる)
    [InlineData(0, 30)]     // シェルフ左上角 (境界は内側扱い)
    [InlineData(340, 570)]  // シェルフ右下角 (境界は内側扱い)
    public void CursorHolds_InsideShelf_True(double x, double y)
    {
        Assert.True(ShelfRetract.CursorHolds(x, y, Shelf, TriggerInside));
    }

    [Theory]
    [InlineData(1, 10)]  // シェルフ上端 (Y=30) より上・トリガー帯 (Y=0..100) の中: トリガー矩形の項が拾う
    [InlineData(3, 0)]   // トリガー矩形自身の右上角 (X=W, Y=0)。境界は内側扱い、かつシェルフの外
                          // (Y=0 < シェルフ上端 30) -- この修正が存在する理由そのものの項を固定する
    public void CursorHolds_InsideTriggerButOutsideShelf_True(double x, double y)
    {
        Assert.True(ShelfRetract.CursorHolds(x, y, Shelf, TriggerSticksOut));
    }

    [Theory]
    [InlineData(341, 300)]  // シェルフの右外
    [InlineData(170, 29)]   // シェルフの上外 (トリガーも外)
    [InlineData(170, 571)]  // シェルフの下外
    [InlineData(4, 10)]     // はみ出しトリガーの右外・シェルフの上外
    public void CursorHolds_OutsideBoth_False(double x, double y)
    {
        Assert.False(ShelfRetract.CursorHolds(x, y, Shelf, TriggerSticksOut));
    }
}
