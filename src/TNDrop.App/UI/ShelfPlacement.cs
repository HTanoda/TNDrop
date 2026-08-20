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
}
