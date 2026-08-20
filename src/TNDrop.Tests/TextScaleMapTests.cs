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
    [InlineData(TextScale.Small, 12, 10)]
    [InlineData(TextScale.Normal, 13, 11)]
    [InlineData(TextScale.Medium, 15, 13)]
    [InlineData(TextScale.Large, 17, 15)]
    public void Resolve_maps_scale_to_base_and_small_sizes(TextScale scale, double expectedBase, double expectedSmall)
    {
        var sizes = TextScaleMap.Resolve(scale);

        Assert.Equal(expectedBase, sizes.Base);
        Assert.Equal(expectedSmall, sizes.Small);
    }

    [Fact]
    public void Apply_writes_both_keys_into_the_given_resource_dictionary()
    {
        var resources = new System.Windows.ResourceDictionary();

        TextScaleMap.Apply(TextScale.Large, resources);

        Assert.Equal(17.0, resources["CardFontSize"]);
        Assert.Equal(15.0, resources["CardSmallFontSize"]);
    }
}
