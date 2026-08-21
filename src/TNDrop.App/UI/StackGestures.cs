using System;
using TNDrop.Core;
using DragDropEffects = System.Windows.DragDropEffects;

namespace TNDrop.UI;

/// <summary>
/// The pure decisions behind the stack gestures (Task 14): where a dragged flyout row has to be
/// released for it to mean "split this file out of the stack", and whether one card may be merged
/// into another.
///
/// <para>Deliberately free of WPF elements and Win32 -- the caller resolves the monitor work area,
/// the cursor position and the drag result, then hands the numbers in here -- so the rules are
/// unit-testable the same way <see cref="ShelfPlacement"/> is.</para>
/// </summary>
public static class StackGestures
{
    /// <summary>
    /// Half-width, in DIPs, of the band along the configured screen edge that turns a released
    /// FLYOUT ROW into a split. The band straddles the edge line: it reaches this far INTO the work
    /// area (over the shelf, which is flush against that edge) and this far past it (a release that
    /// overshot off-screen still counts).
    /// <para>Generous, and safely so: the flyout opens OUTSIDE the shelf (to its right on a
    /// left-edge shelf, see StackFlyout.ShowFor), so a row starts its drag ~350 DIP away from this
    /// band and can only land in it by being deliberately carried there.</para>
    /// </summary>
    public const double SplitEdgeBandDip = 60;

    /// <summary>
    /// Half-width, in DIPs, of the same band when the thing being dragged is the stack CARD itself
    /// (v1.2 Task B's edge-drag extract).
    ///
    /// <para>Deliberately much narrower than <see cref="SplitEdgeBandDip"/>, and NOT a second
    /// predicate -- it is the <c>bandDip</c> argument to the one <see cref="IsInSplitZone"/> below.
    /// The reason is where the gesture STARTS: a card already sits on the shelf, which is flush
    /// against the edge and only 340 DIP wide, with card content beginning ~8 DIP in. At 60 DIP the
    /// band would cover the left ~52 DIP of every card, so a micro-drag released more or less in
    /// place would return None inside the band and silently extract a file. 24 DIP keeps the band a
    /// thin strip at the true screen edge, mostly clear of card content, so reaching it means
    /// actually shoving the card at the edge.</para>
    ///
    /// <para>The flyout keeps 60: its rows have no such proximity problem (see above), and
    /// narrowing a gesture that already works would only make it harder to hit.</para>
    /// </summary>
    public const double CardExtractEdgeBandDip = 24;

    /// <summary>
    /// True when a point -- in the same DIP coordinate space as <paramref name="workArea"/> -- lies
    /// in the split band along <paramref name="edge"/>.
    ///
    /// <para>The band is bounded on BOTH sides of the edge line rather than running off to
    /// infinity outwards. Windows clamps the cursor to the virtual desktop, so "outside" only ever
    /// exists when there is another monitor there -- and a release 800px away on the next monitor
    /// is a drop onto whatever is over there, not a flick at this monitor's edge.</para>
    ///
    /// <para>Vertically the point has to be within the work area (plus the same band as slack, for
    /// a release that clipped the taskbar or the top of the screen). Without that, a release at the
    /// right x on a monitor stacked above or below would read as a split.</para>
    ///
    /// <para><paramref name="bandDip"/> is how the two callers differ, and the ONLY way they differ
    /// -- there is one hit test, parameterized, not two: flyout rows pass
    /// <see cref="SplitEdgeBandDip"/> and the card extract passes
    /// <see cref="CardExtractEdgeBandDip"/>. See those constants for why the widths are not the
    /// same.</para>
    /// </summary>
    public static bool IsInSplitZone(ShelfPlacement.Rect workArea, EdgeSide edge,
                                     double xDip, double yDip, double bandDip = SplitEdgeBandDip)
    {
        var band = Math.Max(1, bandDip);

        if (yDip < workArea.Y - band || yDip > workArea.Y + workArea.H + band)
        {
            return false;
        }

        var edgeX = edge == EdgeSide.Left
            ? workArea.X
            : workArea.X + workArea.W;

        return Math.Abs(xDip - edgeX) <= band;
    }

    /// <summary>
    /// Plain rectangle containment, in the same DIP space as <paramref name="rect"/>. Edges count
    /// as inside: the shelf is flush against the screen edge, so its outermost column of pixels is
    /// exactly where the pointer sits when the user reaches for it.
    ///
    /// <para>Used to answer "is the pointer on the shelf?" from the cursor position rather than
    /// from <c>IsMouseOver</c>. That distinction is load-bearing: while the stack flyout is open it
    /// holds a <c>StaysOpen="False"</c> Popup's SubTree mouse capture, and WPF then reports the
    /// shelf window as NOT moused-over even with the pointer sitting on the card the flyout
    /// belongs to.</para>
    /// </summary>
    public static bool Contains(ShelfPlacement.Rect rect, double xDip, double yDip) =>
        xDip >= rect.X && xDip <= rect.X + rect.W &&
        yDip >= rect.Y && yDip <= rect.Y + rect.H;

    /// <summary>
    /// Whether dropping <paramref name="source"/> onto <paramref name="target"/> is a merge the
    /// shelf should offer -- i.e. whether the drop cursor shows Copy and the target card lights up.
    ///
    /// <para>This is about the KIND combination only. It deliberately does not pre-judge the
    /// 10-file limit: the user is allowed to make the attempt and be told why it was refused
    /// (<see cref="ItemStore.TryMergeFiles"/> returning false, which the shelf answers with a shake
    /// and the StackLimit message), rather than the drop silently reading as "not a drop target"
    /// with no explanation.</para>
    /// </summary>
    public static bool CanAcceptMerge(ClipItem? target, ClipItem? source) =>
        target is not null
        && source is not null
        && !string.Equals(target.Id, source.Id, StringComparison.Ordinal)
        && target.Kind == ClipKind.Files
        && source.Kind == ClipKind.Files;

    /// <summary>
    /// Whether a finished row drag means "split". Both halves are required: the drop must have gone
    /// nowhere (<see cref="DragDropEffects.None"/> -- no external target accepted it, so nothing
    /// else has already acted on the gesture) AND the cursor must have been released in the edge
    /// band. A row dropped into Explorer or Word returns Copy/Link and stays an ordinary one-file
    /// drop, wherever on screen it landed.
    /// </summary>
    public static bool ShouldSplit(DragDropEffects dropResult, bool cursorInSplitZone) =>
        dropResult == DragDropEffects.None && cursorInSplitZone;
}
