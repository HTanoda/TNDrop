using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using TNDrop.Core;

namespace TNDrop.Tests;

/// <summary>
/// Strings.resx (ja) / Strings.en.resx (en) / Strings.cs (the ResourceManager wrapper) must all
/// expose the exact same key set. A resx typo, a missing en translation, or a wrapper property
/// added without updating both resx files has drifted silently before (v1.2, v1.3) and been
/// caught only by a reviewer manually grepping all three files. This test replaces that grep.
///
/// <para>The .resx files compile into an embedded .resources BLOB by the time the test assembly
/// runs, so going through <c>ResourceManager</c> only ever answers "does key X exist", never
/// "what keys exist that shouldn't" or "what's missing". Instead this reads the SOURCE .resx XML
/// straight off disk (System.Xml.Linq), the same way <c>AppVersionTests.FindRepoRoot</c> locates
/// setup.iss/the csproj by walking up from the test assembly's own output directory to the
/// TNDrop.sln marker -- robust to Debug/Release and any future TargetFramework rename.</para>
/// </summary>
public class StringsResxParityTests
{
    [Fact]
    public void Ja_resx_en_resx_and_the_wrapper_expose_the_same_key_set()
    {
        var jaKeys = ReadResxKeys(ResxPath("Strings.resx"));
        var enKeys = ReadResxKeys(ResxPath("Strings.en.resx"));
        var wrapperKeys = WrapperPropertyNames();

        AssertSameKeySet(jaKeys, enKeys, "Strings.resx", "Strings.en.resx");
        AssertSameKeySet(jaKeys, wrapperKeys, "Strings.resx", "Strings.cs (wrapper)");
    }

    /// <summary>
    /// Proves the comparison this test relies on actually FAILS on a real mismatch, rather than
    /// being a no-op that would pass no matter what the resx files say. Runs against synthetic
    /// in-memory sets (not the real resx files) so it stays deterministic regardless of the
    /// current resx content, and does not require ever leaving a real key removed on disk.
    /// </summary>
    [Fact]
    public void AssertSameKeySet_detects_a_key_present_on_only_one_side()
    {
        var withExtraKey = new HashSet<string> { "AppName", "TrayHover", "OnlyOnThisSide" };
        var baseline = new HashSet<string> { "AppName", "TrayHover" };

        var ex = Record.Exception(() => AssertSameKeySet(withExtraKey, baseline, "a", "b"));

        Assert.NotNull(ex);
        Assert.Contains("OnlyOnThisSide", ex!.Message);
    }

    [Fact]
    public void AssertSameKeySet_passes_when_both_sides_agree()
    {
        var a = new HashSet<string> { "AppName", "TrayHover" };
        var b = new HashSet<string> { "TrayHover", "AppName" };

        AssertSameKeySet(a, b, "a", "b"); // must not throw
    }

    private static HashSet<string> ReadResxKeys(string path)
    {
        Assert.True(File.Exists(path), $"resx not found at {path}");

        var doc = XDocument.Load(path);
        // Only direct <data> children of <root> are real string entries -- <resheader> elements
        // (resmimetype/version/reader/writer) also carry a "name" attribute but are resx plumbing,
        // not translatable keys, and must not be counted.
        var keys = doc.Root!.Elements("data")
            .Select(e => e.Attribute("name")?.Value)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(keys);
        return keys;
    }

    private static HashSet<string> WrapperPropertyNames()
    {
        // Strings is `internal static class Strings` (TNDrop.Resources) -- TNDrop.Tests has no
        // InternalsVisibleTo into TNDrop.App, so it is reached via the Type object directly
        // (Assembly.GetType resolves non-public types fine) rather than a compile-time reference.
        var appAssembly = typeof(ClipItem).Assembly;
        var stringsType = appAssembly.GetType("TNDrop.Resources.Strings", throwOnError: true)!;

        var names = stringsType
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(names);
        return names;
    }

    private static void AssertSameKeySet(
        IReadOnlySet<string> a, IReadOnlySet<string> b, string aLabel, string bLabel)
    {
        var onlyInA = a.Except(b).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var onlyInB = b.Except(a).OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.True(onlyInA.Count == 0 && onlyInB.Count == 0,
            $"{aLabel} vs {bLabel} key mismatch. " +
            $"Only in {aLabel}: [{string.Join(", ", onlyInA)}]. " +
            $"Only in {bLabel}: [{string.Join(", ", onlyInB)}].");
    }

    private static string ResxPath(string fileName) =>
        Path.Combine(FindRepoRoot(), "src", "TNDrop.App", "Resources", fileName);

    /// <summary>Walks up from the test assembly's own output directory until it finds
    /// TNDrop.sln, the repo-root marker file -- same approach as
    /// <c>AppVersionTests.FindRepoRoot</c>, robust to Debug/Release and TargetFramework
    /// folder nesting, unlike a fixed count of "..\" segments off AppContext.BaseDirectory.</summary>
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
