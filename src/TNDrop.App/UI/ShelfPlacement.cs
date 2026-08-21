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
    /// beacon reacts to -- "close, but not quite there yet". See <see cref="IsNearTriggerButOutside"/>.</summary>
    public const double HintProximityMarginPx = 8;

    /// <summary>
    /// Whether the trigger proximity hint (v1.2 Task E) should light up: the pointer is within
    /// <paramref name="proximityPx"/> + <see cref="HintProximityMarginPx"/> DIPs of
    /// <paramref name="edge"/> horizontally, on the target monitor vertically, but OUTSIDE
    /// <paramref name="triggerRect"/>'s own vertical span -- "right edge, wrong height".
    ///
    /// <para>A cursor already inside <paramref name="triggerRect"/> (both horizontally and
    /// vertically) returns false: by the time a caller could observe that, WPF's own MouseEnter
    /// has already fired and opened the shelf (see EdgeTriggerWindow), so the hint has nothing
    /// left to hint at.</para>
    /// </summary>
    public static bool IsNearTriggerButOutside(Rect workArea, Rect triggerRect, EdgeSide edge,
                                                int proximityPx, double cursorX, double cursorY)
    {
        var nearWidth = Math.Clamp(proximityPx, 1, 64) + HintProximityMarginPx;

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
