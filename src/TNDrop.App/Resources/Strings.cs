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
    public static string TrayTooltipIncognitoSuffix => Get(nameof(TrayTooltipIncognitoSuffix));
    public static string TraySettings => Get(nameof(TraySettings));
    public static string TrayAbout => Get(nameof(TrayAbout));
    public static string TrayExit => Get(nameof(TrayExit));
    public static string StoreLoadFailed => Get(nameof(StoreLoadFailed));
    public static string AboutText => Get(nameof(AboutText));
    public static string CardImage => Get(nameof(CardImage));
    public static string CardFilesCountFormat => Get(nameof(CardFilesCountFormat));
    public static string CardCharCountFormat => Get(nameof(CardCharCountFormat));
    public static string FilterAll => Get(nameof(FilterAll));
    public static string FilterText => Get(nameof(FilterText));
    public static string FilterLinks => Get(nameof(FilterLinks));
    public static string FilterImages => Get(nameof(FilterImages));
    public static string FilterFiles => Get(nameof(FilterFiles));
    public static string SearchPlaceholder => Get(nameof(SearchPlaceholder));
    public static string ClearButton => Get(nameof(ClearButton));
    public static string ClearConfirmTitle => Get(nameof(ClearConfirmTitle));
    public static string ClearConfirmMessageFormat => Get(nameof(ClearConfirmMessageFormat));
    public static string ActionPin => Get(nameof(ActionPin));
    public static string ActionUnpin => Get(nameof(ActionUnpin));
    public static string ActionDelete => Get(nameof(ActionDelete));
    public static string FileMissing => Get(nameof(FileMissing));
    public static string StackLimit => Get(nameof(StackLimit));
    public static string SelectAll => Get(nameof(SelectAll));
    public static string CopySelected => Get(nameof(CopySelected));
    public static string DeleteSelected => Get(nameof(DeleteSelected));
    public static string ClearSelection => Get(nameof(ClearSelection));
    public static string SelectedCountFormat => Get(nameof(SelectedCountFormat));
    public static string FilesCopiedFormat => Get(nameof(FilesCopiedFormat));

    private static string Get(string key) =>
        Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
}
