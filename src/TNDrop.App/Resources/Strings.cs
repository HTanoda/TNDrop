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

    // Settings window (Task 17). Read once per control at window construction, same as every
    // other consumer in this class -- see the class doc comment on why a language change still
    // needs a restart rather than a live re-read.
    public static string SettingsWindowTitle => Get(nameof(SettingsWindowTitle));
    public static string SettingsNavBehavior => Get(nameof(SettingsNavBehavior));
    public static string SettingsNavPosition => Get(nameof(SettingsNavPosition));
    public static string SettingsNavAppearance => Get(nameof(SettingsNavAppearance));
    public static string SettingsAutoStart => Get(nameof(SettingsAutoStart));
    public static string SettingsSoundsEnabled => Get(nameof(SettingsSoundsEnabled));
    public static string SettingsAutoDelete => Get(nameof(SettingsAutoDelete));
    public static string SettingsAutoDeleteOffOption => Get(nameof(SettingsAutoDeleteOffOption));
    public static string SettingsAutoDeleteHours1Option => Get(nameof(SettingsAutoDeleteHours1Option));
    public static string SettingsAutoDeleteHours6Option => Get(nameof(SettingsAutoDeleteHours6Option));
    public static string SettingsAutoDeleteHours24Option => Get(nameof(SettingsAutoDeleteHours24Option));
    public static string SettingsAutoDeleteDays7Option => Get(nameof(SettingsAutoDeleteDays7Option));
    public static string SettingsMoveToTopOnCopy => Get(nameof(SettingsMoveToTopOnCopy));
    public static string SettingsRetractDelay => Get(nameof(SettingsRetractDelay));
    public static string SettingsEdge => Get(nameof(SettingsEdge));
    public static string SettingsEdgeLeft => Get(nameof(SettingsEdgeLeft));
    public static string SettingsEdgeRight => Get(nameof(SettingsEdgeRight));
    public static string SettingsMonitor => Get(nameof(SettingsMonitor));
    public static string SettingsMonitorAuto => Get(nameof(SettingsMonitorAuto));
    public static string SettingsMonitorFormat => Get(nameof(SettingsMonitorFormat));
    public static string SettingsHotZone => Get(nameof(SettingsHotZone));
    public static string SettingsTriggerSensitivity => Get(nameof(SettingsTriggerSensitivity));
    public static string SettingsTriggerAlign => Get(nameof(SettingsTriggerAlign));
    public static string SettingsTriggerAlignTop => Get(nameof(SettingsTriggerAlignTop));
    public static string SettingsTriggerAlignCenter => Get(nameof(SettingsTriggerAlignCenter));
    public static string SettingsTriggerAlignBottom => Get(nameof(SettingsTriggerAlignBottom));
    public static string SettingsTextScale => Get(nameof(SettingsTextScale));
    public static string SettingsTextScaleSmall => Get(nameof(SettingsTextScaleSmall));
    public static string SettingsTextScaleNormal => Get(nameof(SettingsTextScaleNormal));
    public static string SettingsTextScaleMedium => Get(nameof(SettingsTextScaleMedium));
    public static string SettingsTextScaleLarge => Get(nameof(SettingsTextScaleLarge));
    public static string SettingsIndicatorStyle => Get(nameof(SettingsIndicatorStyle));
    public static string SettingsIndicatorStyleBeacon => Get(nameof(SettingsIndicatorStyleBeacon));
    public static string SettingsIndicatorStyleBar => Get(nameof(SettingsIndicatorStyleBar));
    public static string SettingsIndicatorStylePulse => Get(nameof(SettingsIndicatorStylePulse));
    public static string SettingsIndicatorStyleCorner => Get(nameof(SettingsIndicatorStyleCorner));
    public static string SettingsLanguage => Get(nameof(SettingsLanguage));
    public static string SettingsLanguageJa => Get(nameof(SettingsLanguageJa));
    public static string SettingsLanguageEn => Get(nameof(SettingsLanguageEn));
    public static string SettingsLanguageRestartNote => Get(nameof(SettingsLanguageRestartNote));
    public static string SettingsEdgeHint => Get(nameof(SettingsEdgeHint));
    public static string SettingsPercentFormat => Get(nameof(SettingsPercentFormat));
    public static string SettingsPixelFormat => Get(nameof(SettingsPixelFormat));
    public static string SettingsMillisecondsFormat => Get(nameof(SettingsMillisecondsFormat));

    private static string Get(string key) =>
        Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
}
