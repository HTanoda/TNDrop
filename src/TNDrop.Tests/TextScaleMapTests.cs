using TNDrop.Core;
using TNDrop.UI;

namespace TNDrop.Tests;

/// <summary>
/// Pins the exact point sizes each <see cref="TextScale"/> value maps to, so a future edit to
/// TextScaleMap.Resolve cannot silently change what "小/標準/中/大" mean without a test noticing.
/// </summary>
public class TextScaleMapTests
{
    [Theory]
    [InlineData(TextScale.Small, 12, 10, 48)]
    [InlineData(TextScale.Normal, 13, 11, 52)]
    [InlineData(TextScale.Medium, 15, 13, 60)]
    [InlineData(TextScale.Large, 17, 15, 68)]
    public void Resolve_maps_scale_to_base_small_and_text_max_height(
        TextScale scale, double expectedBase, double expectedSmall, double expectedTextMaxHeight)
    {
        var sizes = TextScaleMap.Resolve(scale);

        Assert.Equal(expectedBase, sizes.Base);
        Assert.Equal(expectedSmall, sizes.Small);
        Assert.Equal(expectedTextMaxHeight, sizes.TextMaxHeight);
    }

    [Fact]
    public void Apply_writes_all_three_keys_into_the_given_resource_dictionary()
    {
        var resources = new System.Windows.ResourceDictionary();

        TextScaleMap.Apply(TextScale.Large, resources);

        Assert.Equal(17.0, resources["CardFontSize"]);
        Assert.Equal(15.0, resources["CardSmallFontSize"]);
        Assert.Equal(68.0, resources["CardTextMaxHeight"]);
    }
}
