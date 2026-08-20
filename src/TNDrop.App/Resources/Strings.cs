using System.Globalization;
using System.Reflection;
using System.Resources;

namespace TNDrop.Resources;

/// <summary>
/// Thin wrapper over the embedded Strings.resx / Strings.en.resx resources. Looked up
/// against <see cref="CultureInfo.CurrentUICulture"/> at call time, so switching
/// CurrentUICulture (done once at startup from AppSettings.Language) is enough --
/// no restart required for the strings themselves.
/// </summary>
internal static class Strings
{
    private static readonly ResourceManager Manager =
        new("TNDrop.Resources.Strings", Assembly.GetExecutingAssembly());

    public static string AppName => Get(nameof(AppName));
    public static string TrayHover => Get(nameof(TrayHover));
    public static string TrayIncognito => Get(nameof(TrayIncognito));
    public static string TraySettings => Get(nameof(TraySettings));
    public static string TrayAbout => Get(nameof(TrayAbout));
    public static string TrayExit => Get(nameof(TrayExit));
    public static string StoreLoadFailed => Get(nameof(StoreLoadFailed));
    public static string AboutText => Get(nameof(AboutText));

    private static string Get(string key) =>
        Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
}
