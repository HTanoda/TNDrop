using System;
using TNDrop.Core;

namespace TNDrop.UI;

/// <summary>
/// Pure geometry for the edge trigger band and the shelf, in DIPs. Kept free of WPF and Win32
/// types so the layout rules are unit-testable: callers resolve the monitor and the DPI, then
/// hand the resulting work area in here.
/// </summary>
public static class ShelfPlacement
{
    /// <summary>Shelf width in DIPs. Fixed by design.</summary>
    public const double ShelfWidth = 340;

    /// <summary>Shelf height as a fraction of the work area height.</summary>
    public const double ShelfHeightRatio = 0.9;

    public record struct Rect(double X, double Y, double W, double H);

    /// <summary>
    /// Position of the invisible hover band along <paramref name="edge"/> of
    /// <paramref name="workArea"/>. Width is the pointer proximity in DIPs; height is
    /// <paramref name="hotZonePercent"/> of the work area height, placed per
    /// <paramref name="align"/>. Inputs are clamped so a corrupt settings file cannot produce a
    /// zero-sized (unhittable) or oversized band.
    /// </summary>
    public static Rect TriggerRect(Rect workArea, EdgeSide edge, int proximityPx,
                                   int hotZonePercent, TriggerAlign align)
    {
        var width = Math.Clamp(proximityPx, 1, 64);
        var percent = Math.Clamp(hotZonePercent, 1, 100);

        // Integer numerator first: 1040 * 40 / 100.0 is exactly 416, unlike 1040 * 0.40.
        var height = workArea.H * percent / 100.0;

        var x = edge == EdgeSide.Left
            ? workArea.X
            : workArea.X + workArea.W - width;

        var y = align switch
        {
            TriggerAlign.Top => workArea.Y,
            TriggerAlign.Bottom => workArea.Y + workArea.H - height,
            _ => workArea.Y + (workArea.H - height) / 2.0,
        };

        return new Rect(x, y, width, height);
    }

    /// <summary>Shelf position when shown: flush against <paramref name="edge"/>, vertically centred.</summary>
    public static Rect ShelfRect(Rect workArea, EdgeSide edge)
    {
        var height = workArea.H * ShelfHeightRatio;
        var x = edge == EdgeSide.Left
            ? workArea.X
            : workArea.X + workArea.W - ShelfWidth;
        var y = workArea.Y + (workArea.H - height) / 2.0;
        return new Rect(x, y, ShelfWidth, height);
    }

    /// <summary>Shelf X when hidden: one full shelf width off-screen past <paramref name="edge"/>.</summary>
    public static double HiddenX(Rect shelfRect, EdgeSide edge)
        => edge == EdgeSide.Left
            ? shelfRect.X - shelfRect.W
            : shelfRect.X + shelfRect.W;

    /// <summary>Extra margin (DIPs) beyond the trigger band's own proximity width that the hint
    /// beacon reacts to -- "close, but not quite there yet". See <see cref="IsNearTriggerButOutside"/>.
    /// v1.4.1 Task A: widened 8->24 -- at typical sensitivity settings the old margin produced a
    /// near-band only ~11px wide (triggerRect.W, itself clamped to [1,64], plus 8), which users
    /// reported as too thin to ever land the cursor in. 24 alone is still not enough at the
    /// smallest sensitivities, which is what <see cref="HintMinBandDip"/> is for.</summary>
    public const double HintProximityMarginPx = 24;

    /// <summary>Minimum near-band width (DIPs), regardless of how thin <paramref name="triggerRect"/>
    /// (and therefore <see cref="HintProximityMarginPx"/>'s base) is. v1.4.1 Task A: without this
    /// floor, a user with TriggerProximityPx clamped near its minimum (1) would still get a near-band
    /// of only ~25px (1 + 24) -- workable but tighter than the ~30px this floor guarantees. See
    /// <see cref="IsNearTriggerButOutside"/>.</summary>
    public const double HintMinBandDip = 30;

    /// <summary>
    /// Whether the trigger proximity hint (v1.2 Task E) should light up: the pointer is within the
    /// near-band (<paramref name="triggerRect"/>'s own width plus <see cref="HintProximityMarginPx"/>
    /// DIPs, floored at <see cref="HintMinBandDip"/>) of <paramref name="edge"/> horizontally, on the
    /// target monitor vertically, but OUTSIDE <paramref name="triggerRect"/>'s own vertical span --
    /// "right edge, wrong height".
    ///
    /// <para>Takes <paramref name="triggerRect"/>'s width as the one source of the real band's
    /// size rather than re-deriving it from a separate <c>proximityPx</c> parameter --
    /// <see cref="TriggerRect"/> already clamps that value; a second clamp here computing the same
    /// quantity a second way is exactly the kind of drift a settings change to the clamp range
    /// could silently break. The width formula below (margin plus floor) is likewise resolved in
    /// this ONE place; nothing else in the codebase computes a near-band width.</para>
    ///
    /// <para>The exclusion check below only tests the VERTICAL span, not horizontal, and that is
    /// deliberate rather than an oversight: <paramref name="triggerRect"/>'s width is entirely
    /// contained within the near-band tested first (the near-band adds a margin on top of it, never
    /// subtracts), so any cursor that is both within the near-band AND within the trigger rect's
    /// vertical span is -- by that containment -- also within the real trigger rect. There is no
    /// horizontal case left to reject that the near-band and vertical checks don't already cover.
    /// Such a cursor would have already fired WPF's own MouseEnter and opened the shelf (see
    /// EdgeTriggerWindow), so the hint has nothing left to hint at by the time anything could ask
    /// this function about it. The floor does not change this reasoning: it only ever WIDENS the
    /// near-band relative to triggerRect.W, so the containment argument still holds.</para>
    /// </summary>
    public static bool IsNearTriggerButOutside(Rect workArea, Rect triggerRect, EdgeSide edge,
                                                double cursorX, double cursorY)
    {
        var nearWidth = Math.Max(HintMinBandDip, triggerRect.W + HintProximityMarginPx);

        var distFromEdge = edge == EdgeSide.Left
            ? cursorX - workArea.X
            : (workArea.X + workArea.W) - cursorX;

        if (distFromEdge < 0 || distFromEdge > nearWidth)
        {
            return false;
        }

        if (cursorY < workArea.Y || cursorY > workArea.Y + workArea.H)
        {
            return false;
        }

        var insideHotZone = cursorY >= triggerRect.Y && cursorY <= triggerRect.Y + triggerRect.H;
        return !insideHotZone;
    }
}
