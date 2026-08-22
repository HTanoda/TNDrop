using TNDrop.UI;
using Xunit;

namespace TNDrop.Tests;

/// <summary>
/// HelpLauncher.MissingReasonCode is the one pure decision behind the header's "?" button
/// (v1.3.1): whether the bundled README.html exists is turned into a reason code (or null) here,
/// separately from the I/O (File.Exists, Process.Start) that ShelfWindow.OnHelpButtonClick itself
/// performs. See that method's doc comment for why the split exists.
/// </summary>
public class HelpLauncherTests
{
    [Fact]
    public void MissingReasonCode_is_null_when_the_readme_exists()
        => Assert.Null(HelpLauncher.MissingReasonCode(readmeExists: true));

    [Fact]
    public void MissingReasonCode_is_not_found_when_the_readme_is_missing()
        => Assert.Equal("not-found", HelpLauncher.MissingReasonCode(readmeExists: false));

    [Fact]
    public void ReadmeFileName_is_a_bare_file_name_not_a_path()
    {
        // Must stay a bare name: ShelfWindow.OnHelpButtonClick joins it onto
        // AppContext.BaseDirectory itself, and a value that already looked like a path here would
        // double up the directory or break Path.Combine's semantics.
        Assert.DoesNotContain('\\', HelpLauncher.ReadmeFileName);
        Assert.DoesNotContain('/', HelpLauncher.ReadmeFileName);
        Assert.Equal("README.html", HelpLauncher.ReadmeFileName);
    }
}
