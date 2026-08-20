using System;
using System.Windows;
using TNDrop.Core;

namespace TNDrop.UI;

/// <summary>
/// The one place that turns <see cref="TextScale"/> into actual point sizes. Both
/// <c>App.OnStartup</c> (applying the saved setting before any window is shown) and
/// <see cref="SettingsWindow"/> (applying a live change) call <see cref="Apply"/> so the two
/// can never compute different sizes for the same enum value -- resolving the mapping in only
/// one place is what keeps them from drifting apart if one of the two call sites is edited later
/// without the other.
/// </summary>
public static class TextScaleMap
{
    /// <summary>Line-height factor for the default WPF/Segoe UI "Ideal" text formatting -- an
    /// unstyled line of text renders at roughly 1.34x its point size tall. Used only to derive
    /// <see cref="Sizes.TextMaxHeight"/> below; not a font metric TNDrop reads at runtime.</summary>
    private const double LineHeightFactor = 1.34;

    /// <summary>Number of lines Cards.xaml's TextContent clamps a Text card to before ellipsizing.
    /// Must match Cards.xaml's own comment ("up to 3 lines, ellipsis") -- if that clamp ever
    /// changes, this is the only other place that needs to change with it.</summary>
    private const int TextCardLines = 3;

    /// <summary>
    /// <paramref name="Base"/> is the main card/search-box text size; <paramref name="Small"/> is
    /// the secondary text size (subtitles, the footer status line, the batch-bar/filter-tab
    /// labels); <paramref name="TextMaxHeight"/> is the pixel clamp a 3-line Text card's TextBlock
    /// needs at that point size so the third line trims with an ellipsis instead of having its
    /// glyph tops sliced off by a MaxHeight sized for a smaller, unscaled font.
    /// </summary>
    public readonly record struct Sizes(double Base, double Small, double TextMaxHeight);

    public static Sizes Resolve(TextScale scale)
    {
        var (basePt, smallPt) = scale switch
        {
            TextScale.Small => (12.0, 10.0),
            TextScale.Normal => (13.0, 11.0),
            TextScale.Medium => (15.0, 13.0),
            TextScale.Large => (17.0, 15.0),
            _ => (13.0, 11.0),
        };

        var textMaxHeight = Math.Round(basePt * LineHeightFactor * TextCardLines, MidpointRounding.AwayFromZero);
        return new Sizes(basePt, smallPt, textMaxHeight);
    }

    /// <summary>
    /// Writes <see cref="Sizes.Base"/>/<see cref="Sizes.Small"/>/<see cref="Sizes.TextMaxHeight"/>
    /// into <paramref name="resources"/> under the "CardFontSize"/"CardSmallFontSize"/
    /// "CardTextMaxHeight" keys that Cards.xaml and ShelfWindow.xaml reference via
    /// DynamicResource. A plain resource assignment (not a style/theme swap) is enough:
    /// DynamicResource re-reads its key on every resource-dictionary change, so every
    /// FontSize/MaxHeight bound to it repaints immediately.
    /// </summary>
    public static void Apply(TextScale scale, ResourceDictionary resources)
    {
        var sizes = Resolve(scale);
        resources["CardFontSize"] = sizes.Base;
        resources["CardSmallFontSize"] = sizes.Small;
        resources["CardTextMaxHeight"] = sizes.TextMaxHeight;
    }
}
