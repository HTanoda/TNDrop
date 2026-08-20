using System.Text.RegularExpressions;
using TNDrop.Core;

namespace TNDrop.Tests;

/// <summary>
/// AppVersion.Display is the single source every About/footer consumer formats into its text
/// (Task D, v1.1) -- it must resolve from TNDrop.App's own assembly version, not a literal that
/// could drift from the csproj at the next release bump.
/// </summary>
public class AppVersionTests
{
    [Fact]
    public void Display_is_a_three_part_version_string()
        => Assert.Matches(new Regex(@"^\d+\.\d+\.\d+$"), AppVersion.Display);

    [Fact]
    public void Display_matches_the_TNDrop_App_assembly_version()
    {
        var assemblyVersion = typeof(AppVersion).Assembly.GetName().Version;
        Assert.NotNull(assemblyVersion);
        Assert.Equal(assemblyVersion!.ToString(3), AppVersion.Display);
    }
}
