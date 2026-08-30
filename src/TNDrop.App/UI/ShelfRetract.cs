namespace TNDrop.UI;

/// <summary>
/// The pure decision behind "should the shelf's retract countdown be RUNNING right now?", lifted
/// out of <see cref="ShelfWindow"/> so it can be tested without a window, a dispatcher or a mouse.
///
/// <para>Separate from "should the shelf retract right now?" -- that question is
/// <see cref="ShelfWindow.IsPointerInside"/>, re-asked on every tick. This one is about whether a
/// timer exists at all to ask it again later, which is the failure the v1.2 Task B probe caught:
/// a shelf held open by a term that expires on its own had no timer armed, so nothing was ever
/// going to notice the expiry, and the shelf stayed on screen with no way for the user to dismiss
/// it (the trigger band is hidden while the shelf is out).</para>
/// </summary>
public static class ShelfRetract
{
    /// <summary>
    /// Whether <c>ShelfWindow.ArmRetractIfPointerOutside</c> should start the countdown.
    ///
    /// <para><paramref name="pointerInside"/> is the composite
    /// <see cref="ShelfWindow.IsPointerInside"/> -- pointer, outbound drag, drag-over, stack
    /// flyout, keyboard focus AND the drag-open grace. <paramref name="dragOpenGraceActive"/> then
    /// names that last term a second time ON PURPOSE, because it is the only one of them that goes
    /// away with no event behind it:</para>
    /// <list type="bullet">
    /// <item>the pointer leaving raises MouseLeave,</item>
    /// <item>a drag leaving raises DragLeave,</item>
    /// <item>the flyout closing raises Closed,</item>
    /// </list>
    /// <para>and every one of those comes back through the arming call. A deadline just passes. So
    /// while the grace is what is holding the shelf, the countdown has to run ANYWAY -- the tick
    /// handler's suppress-and-re-arm loop is then what turns the expiry into a retract.</para>
    ///
    /// <para>A hidden shelf never arms: it has nothing to retract, and a live timer would fire
    /// against the next appearance instead.</para>
    ///
    /// <para><paramref name="pinned"/> (v1.5 追補) overrides every other term: while pinned, no
    /// countdown is ever armed, regardless of pointer or grace state. Pinned means "suppress
    /// auto-retract"; an explicit close (the shelf's × button / SlideOut) does not go through this
    /// rule at all, so pinning never blocks the user from closing the shelf on purpose.</para>
    /// </summary>
    public static bool ShouldArm(bool isVisible, bool pinned, bool pointerInside, bool dragOpenGraceActive)
    {
        if (!isVisible)
        {
            return false;
        }

        // v1.5: ピン中は何があってもカウントダウンを起動しない。ピンは「自動格納の抑止」
        // だけを意味し、明示的な閉じる (× / SlideOut 呼び出し) はこの関数を通らないので
        // 従来どおり効く。ピン中のシェルフは × で常に閉じられるため、「タイマーのない
        // 可視シェルフは詰む」という上の段落の前提はピンには当てはまらない。
        if (pinned)
        {
            return false;
        }

        return !pointerInside || dragOpenGraceActive;
    }

    /// <summary>
    /// v1.7.1: "is the cursor PHYSICALLY over the shelf or the trigger band right now?" --
    /// answered from coordinates, not mouse events. A stationary cursor generates no
    /// WM_MOUSEMOVE, so a shelf that slides in underneath it never receives MouseEnter and
    /// every event-derived term of IsPointerInside stays false; the retract tick then slides
    /// the shelf out from under a cursor that is sitting on it, the trigger band comes back,
    /// and the loop repeats (same failure class StackFlyout measured and fixed with
    /// CursorOverShelf). This is the tick-side suppression term only -- arming is untouched,
    /// so the timer keeps running as the poll that re-asks this every tick.
    /// <para>The trigger rect is checked as well as the shelf rect because Top/Bottom align
    /// with a large hot zone can push the band outside the shelf's vertical span; "the cursor
    /// is parked on the trigger" must hold the shelf open in every configuration.</para>
    /// <para>Boundaries count as inside: erring toward "holding" keeps the shelf open one
    /// tick longer, which is the harmless direction.</para>
    /// </summary>
    public static bool CursorHolds(double cursorX, double cursorY,
        ShelfPlacement.Rect shelfRect, ShelfPlacement.Rect triggerRect)
    {
        // StackGestures.Contains is the single point-in-rect authority (also used by
        // IsCursorOverShelf) -- delegate rather than re-deriving the same inclusive-bounds test.
        return StackGestures.Contains(shelfRect, cursorX, cursorY) ||
               StackGestures.Contains(triggerRect, cursorX, cursorY);
    }
}
