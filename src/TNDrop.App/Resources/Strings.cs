using System.Globalization;
using System.Reflection;
using System.Resources;

namespace TNDrop.Resources;

/// <summary>
/// Thin wrapper over the embedded Strings.resx / Strings.en.resx resources. Each property
/// resolves against <see cref="CultureInfo.CurrentUICulture"/> at the moment it is read --
/// but that does not make the UI language switch live. Consumers such as
/// TNDrop.UI.TrayIcon read these properties once, at construction, and bake the resulting
/// strings into their menu items/text; they do not re-read on a later culture change. So a
/// change to AppSettings.Language only takes effect the next time the app starts, which
/// matches the rest of the settings design (see App.OnStartup, which sets CurrentUICulture
/// once during startup, before any of these consumers are constructed).
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
