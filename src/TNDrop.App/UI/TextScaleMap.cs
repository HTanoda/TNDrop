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
    /// <summary>
    /// <paramref name="Base"/> is the main card/search-box text size; <paramref name="Small"/> is
    /// the secondary text size (subtitles, the footer status line). Kept to just these two --
    /// per the brief, one base size plus one small size is enough to scale the shelf's reading
    /// text without a size key per label.
    /// </summary>
    public readonly record struct Sizes(double Base, double Small);

    public static Sizes Resolve(TextScale scale) => scale switch
    {
        TextScale.Small => new Sizes(12, 10),
        TextScale.Normal => new Sizes(13, 11),
        TextScale.Medium => new Sizes(15, 13),
        TextScale.Large => new Sizes(17, 15),
        _ => new Sizes(13, 11),
    };

    /// <summary>
    /// Writes <see cref="Sizes.Base"/>/<see cref="Sizes.Small"/> into
    /// <paramref name="resources"/> under the "CardFontSize"/"CardSmallFontSize" keys that
    /// Cards.xaml and ShelfWindow.xaml reference via DynamicResource. A plain resource
    /// assignment (not a style/theme swap) is enough: DynamicResource re-reads its key on every
    /// resource-dictionary change, so every FontSize bound to it repaints immediately.
    /// </summary>
    public static void Apply(TextScale scale, ResourceDictionary resources)
    {
        var sizes = Resolve(scale);
        resources["CardFontSize"] = sizes.Base;
        resources["CardSmallFontSize"] = sizes.Small;
    }
}
