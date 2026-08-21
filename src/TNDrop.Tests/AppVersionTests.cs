using System;
using System.IO;
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

    /// <summary>
    /// The installer's own version string (setup.iss's MyAppVersion, baked into the output
    /// filename and the Add/Remove Programs entry) must never drift from TNDrop.App.csproj's
    /// &lt;Version&gt; -- a v1.1 release-day mismatch would ship an installer claiming the wrong
    /// version. Locates both files by walking up from the test assembly's own directory looking
    /// for the marker file TNDrop.sln, rather than assuming a fixed number of "..\" segments from
    /// AppContext.BaseDirectory -- that count changes with Debug/Release and target framework
    /// folder nesting, and has broken this kind of repo-relative lookup before.
    /// </summary>
    [Fact]
    public void Csproj_version_matches_setup_iss_version()
    {
        var repoRoot = FindRepoRoot();

        var csprojPath = Path.Combine(repoRoot, "src", "TNDrop.App", "TNDrop.App.csproj");
        var setupIssPath = Path.Combine(repoRoot, "installer", "setup.iss");

        Assert.True(File.Exists(csprojPath), $"csproj not found at {csprojPath}");
        Assert.True(File.Exists(setupIssPath), $"setup.iss not found at {setupIssPath}");

        var csprojVersion = ExtractFirstGroup(
            File.ReadAllText(csprojPath), @"<Version>\s*([^<\s]+)\s*</Version>", csprojPath);
        var setupIssVersion = ExtractFirstGroup(
            File.ReadAllText(setupIssPath), @"#define\s+MyAppVersion\s+""([^""]+)""", setupIssPath);

        Assert.Equal(csprojVersion, setupIssVersion);
    }

    private static string ExtractFirstGroup(string text, string pattern, string sourcePath)
    {
        var match = Regex.Match(text, pattern);
        Assert.True(match.Success, $"pattern '{pattern}' not found in {sourcePath}");
        return match.Groups[1].Value;
    }

    /// <summary>Walks up from the test assembly's own output directory until it finds TNDrop.sln,
    /// the repo-root marker file. Robust to Debug/Release and any future TargetFramework rename,
    /// unlike a fixed count of "..\" segments off AppContext.BaseDirectory.</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TNDrop.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find TNDrop.sln walking up from {AppContext.BaseDirectory}");
    }
}
