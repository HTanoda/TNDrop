using System.Reflection;

namespace TNDrop.Core;

/// <summary>
/// The single place that resolves "what version is this build" for display purposes (About
/// dialog, settings footer). Reads the running assembly's version rather than a hardcoded
/// literal, so every consumer moves together at the next release bump instead of drifting the
/// way the old resx-embedded "TNDrop v1.0.0" string did (Task D, v1.1).
/// </summary>
public static class AppVersion
{
    /// <summary>
    /// The 3-part "major.minor.build" version string derived from
    /// <c>&lt;Version&gt;</c> in TNDrop.App.csproj at build time (via the SDK-generated
    /// AssemblyVersion attribute). E.g. "1.1.0" for a csproj &lt;Version&gt;1.1.0&lt;/Version&gt;.
    /// Never null/empty: falls back to "0.0.0" only if the executing assembly somehow has no
    /// version at all, which does not happen for a normally built TNDrop.exe.
    /// </summary>
    public static string Display { get; } = ResolveDisplay();

    private static string ResolveDisplay()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? "0.0.0" : version.ToString(3);
    }
}
