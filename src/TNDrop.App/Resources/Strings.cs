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
    public static string TrayBackup => Get(nameof(TrayBackup));
    public static string TrayAbout => Get(nameof(TrayAbout));
    public static string TrayExit => Get(nameof(TrayExit));
    public static string StoreLoadFailed => Get(nameof(StoreLoadFailed));
    public static string AboutText => Get(nameof(AboutText));
    public static string CardImage => Get(nameof(CardImage));

    // Friendly blob file naming for a converted/materialized image (v1.3 Task B): the base word
    // BlobNaming.FriendlyImageFileName appends a local-time timestamp to.
    public static string ScreenshotFileBaseName => Get(nameof(ScreenshotFileBaseName));
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
    public static string SettingsPurgeUnpinnedOnRestart => Get(nameof(SettingsPurgeUnpinnedOnRestart));
    public static string SettingsHistoryCapacity => Get(nameof(SettingsHistoryCapacity));
    public static string SettingsHistoryCapacityFormat => Get(nameof(SettingsHistoryCapacityFormat));
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
    public static string SettingsIndicatorStyleBulge => Get(nameof(SettingsIndicatorStyleBulge));
    public static string SettingsIndicatorColor => Get(nameof(SettingsIndicatorColor));
    public static string SettingsIndicatorColorDesc => Get(nameof(SettingsIndicatorColorDesc));
    public static string SettingsIndicatorOpacity => Get(nameof(SettingsIndicatorOpacity));
    public static string SettingsIndicatorOpacityValueFormat => Get(nameof(SettingsIndicatorOpacityValueFormat));
    public static string SettingsIndicatorOpacityDesc => Get(nameof(SettingsIndicatorOpacityDesc));
    public static string SettingsIndicatorColorSkyBlue => Get(nameof(SettingsIndicatorColorSkyBlue));
    public static string SettingsIndicatorColorBlue => Get(nameof(SettingsIndicatorColorBlue));
    public static string SettingsIndicatorColorGreen => Get(nameof(SettingsIndicatorColorGreen));
    public static string SettingsIndicatorColorOrange => Get(nameof(SettingsIndicatorColorOrange));
    public static string SettingsIndicatorColorRed => Get(nameof(SettingsIndicatorColorRed));
    public static string SettingsIndicatorColorPurple => Get(nameof(SettingsIndicatorColorPurple));
    public static string SettingsIndicatorColorWhite => Get(nameof(SettingsIndicatorColorWhite));
    public static string SettingsIndicatorColorGray => Get(nameof(SettingsIndicatorColorGray));
    public static string SettingsIndicatorEnabled => Get(nameof(SettingsIndicatorEnabled));
    public static string SettingsLanguage => Get(nameof(SettingsLanguage));
    public static string SettingsLanguageJa => Get(nameof(SettingsLanguageJa));
    public static string SettingsLanguageEn => Get(nameof(SettingsLanguageEn));
    public static string SettingsLanguageRestartNote => Get(nameof(SettingsLanguageRestartNote));
    public static string SettingsTriggerHint => Get(nameof(SettingsTriggerHint));
    public static string SettingsPercentFormat => Get(nameof(SettingsPercentFormat));
    public static string SettingsPixelFormat => Get(nameof(SettingsPixelFormat));
    public static string SettingsMillisecondsFormat => Get(nameof(SettingsMillisecondsFormat));
    public static string SettingsHoverEnabled => Get(nameof(SettingsHoverEnabled));
    public static string SettingsAutoBackup => Get(nameof(SettingsAutoBackup));
    public static string SettingsOpenBackupDialog => Get(nameof(SettingsOpenBackupDialog));

    // Shelf header + footer (v1.1 Task C): the ⚙/× buttons' tooltips and the card-count line.
    public static string HeaderSettingsTooltip => Get(nameof(HeaderSettingsTooltip));
    public static string HeaderHideTooltip => Get(nameof(HeaderHideTooltip));

    // Pin button (v1.5 addendum): toggles auto-retract suppression -- see ShelfWindow's
    // UpdatePinButtonVisual, which picks between these two based on _pinned.
    public static string HeaderPinTooltip => Get(nameof(HeaderPinTooltip));
    public static string HeaderPinActiveTooltip => Get(nameof(HeaderPinActiveTooltip));
    public static string TotalCountFormat => Get(nameof(TotalCountFormat));
    public static string FilteredCountFormat => Get(nameof(FilteredCountFormat));

    // Help button (v1.3.1): opens the bundled README.html. See ShelfWindow.OnHelpButtonClick and
    // HelpLauncher for the tooltip/failure-message split.
    public static string HelpButtonTooltip => Get(nameof(HelpButtonTooltip));
    public static string HelpOpenFailed => Get(nameof(HelpOpenFailed));

    // Pinned accordion + click-to-paste (v1.2 Task H).
    public static string PinnedHeaderFormat => Get(nameof(PinnedHeaderFormat));
    public static string PinnedToggleTooltip => Get(nameof(PinnedToggleTooltip));

    // Search clear button (v1.2 Task F).
    public static string SearchClearTooltip => Get(nameof(SearchClearTooltip));
    public static string SettingsPasteOnClick => Get(nameof(SettingsPasteOnClick));
    public static string SettingsPasteOnClickHint => Get(nameof(SettingsPasteOnClickHint));

    // Per-item description lines (v1.2 Task C), one per setting across all three categories --
    // generalizes the SettingsPasteOnClickHint pattern above. Always the fixed-size, explicit-
    // color Settings.HintText style, never TextScale-linked (see that style's own comment).
    public static string SettingsAutoStartDesc => Get(nameof(SettingsAutoStartDesc));
    public static string SettingsSoundsEnabledDesc => Get(nameof(SettingsSoundsEnabledDesc));
    public static string SettingsIncognitoDesc => Get(nameof(SettingsIncognitoDesc));
    public static string SettingsHoverEnabledDesc => Get(nameof(SettingsHoverEnabledDesc));
    public static string SettingsAutoDeleteDesc => Get(nameof(SettingsAutoDeleteDesc));
    public static string SettingsPurgeUnpinnedOnRestartDesc => Get(nameof(SettingsPurgeUnpinnedOnRestartDesc));
    public static string SettingsHistoryCapacityDesc => Get(nameof(SettingsHistoryCapacityDesc));
    public static string SettingsMoveToTopOnCopyDesc => Get(nameof(SettingsMoveToTopOnCopyDesc));
    public static string SettingsRetractDelayDesc => Get(nameof(SettingsRetractDelayDesc));
    public static string SettingsEdgeDesc => Get(nameof(SettingsEdgeDesc));
    public static string SettingsMonitorDesc => Get(nameof(SettingsMonitorDesc));
    public static string SettingsHotZoneDesc => Get(nameof(SettingsHotZoneDesc));
    public static string SettingsTriggerSensitivityDesc => Get(nameof(SettingsTriggerSensitivityDesc));
    public static string SettingsTriggerAlignDesc => Get(nameof(SettingsTriggerAlignDesc));
    public static string SettingsTriggerHintDesc => Get(nameof(SettingsTriggerHintDesc));
    public static string SettingsTextScaleDesc => Get(nameof(SettingsTextScaleDesc));
    public static string SettingsIndicatorEnabledDesc => Get(nameof(SettingsIndicatorEnabledDesc));
    public static string SettingsIndicatorStyleDesc => Get(nameof(SettingsIndicatorStyleDesc));
    public static string SettingsLanguageDesc => Get(nameof(SettingsLanguageDesc));

    // Stack flyout (v1.3 Task C): explicit ungroup UI, replacing the hidden edge-band drag as the
    // primary path. CardFilesCountFormat ("ファイル {0} 件") is reused for the header's own count
    // text rather than adding a near-duplicate string -- same phrase already shown on a stack card.
    public static string FlyoutUngroupAll => Get(nameof(FlyoutUngroupAll));
    public static string FlyoutSplitOneTooltip => Get(nameof(FlyoutSplitOneTooltip));

    // Backup/migration dialog + password dialog (v1.6 Task 7). See BackupDialog/PasswordDialog.
    public static string BackupDialogTitle => Get(nameof(BackupDialogTitle));
    public static string BackupListLabel => Get(nameof(BackupListLabel));
    public static string BackupManualButton => Get(nameof(BackupManualButton));
    public static string BackupRestoreButton => Get(nameof(BackupRestoreButton));
    public static string BackupDeleteButton => Get(nameof(BackupDeleteButton));
    public static string BackupKindAuto => Get(nameof(BackupKindAuto));
    public static string BackupKindManual => Get(nameof(BackupKindManual));
    public static string BackupKindSafety => Get(nameof(BackupKindSafety));
    public static string BackupAutoHintOn => Get(nameof(BackupAutoHintOn));
    public static string BackupAutoHintOff => Get(nameof(BackupAutoHintOff));
    public static string BackupMigrationLabel => Get(nameof(BackupMigrationLabel));
    public static string BackupExportButton => Get(nameof(BackupExportButton));
    public static string BackupImportButton => Get(nameof(BackupImportButton));
    public static string BackupRestoreConfirm => Get(nameof(BackupRestoreConfirm));
    public static string BackupDeleteConfirm => Get(nameof(BackupDeleteConfirm));
    public static string BackupManualFailed => Get(nameof(BackupManualFailed));
    public static string BackupRestoreDone => Get(nameof(BackupRestoreDone));
    public static string BackupRestoreAborted => Get(nameof(BackupRestoreAborted));
    public static string BackupRestoreFailedRolledBack => Get(nameof(BackupRestoreFailedRolledBack));
    public static string BackupRestoreFailedFatalFormat => Get(nameof(BackupRestoreFailedFatalFormat));
    public static string BackupWrongEnvironment => Get(nameof(BackupWrongEnvironment));
    public static string BackupNotABackup => Get(nameof(BackupNotABackup));
    public static string ExportDoneFormat => Get(nameof(ExportDoneFormat));
    public static string ExportFailed => Get(nameof(ExportFailed));
    public static string ImportWrongPassword => Get(nameof(ImportWrongPassword));
    public static string ImportNotExportFile => Get(nameof(ImportNotExportFile));
    public static string ExportPasswordTitle => Get(nameof(ExportPasswordTitle));
    public static string ImportPasswordTitle => Get(nameof(ImportPasswordTitle));
    public static string PasswordLabel => Get(nameof(PasswordLabel));
    public static string PasswordConfirmLabel => Get(nameof(PasswordConfirmLabel));
    public static string PasswordHint => Get(nameof(PasswordHint));
    public static string PasswordTooShort => Get(nameof(PasswordTooShort));
    public static string PasswordMismatch => Get(nameof(PasswordMismatch));
    public static string PasswordOk => Get(nameof(PasswordOk));
    public static string PasswordCancel => Get(nameof(PasswordCancel));
    public static string ExportFileFilter => Get(nameof(ExportFileFilter));

    private static string Get(string key) =>
        Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
}
