using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using TNDrop.Core;
using TNDrop.Resources;
using TNDrop.Services;
using MessageBox = System.Windows.MessageBox;

namespace TNDrop.UI;

/// <summary>
/// The backup / migration dialog (v1.6 Task 7, design doc §5/§6): a list of backups (auto/manual/
/// safety) with manual-create/restore/delete, plus a separate export/import row for moving data
/// to another PC via a password-protected .tndexport file. Opened single-instance from the tray
/// (see App.OnBackupDialogRequested, which mirrors OnOpenSettingsRequested).
///
/// <para>This window never calls <see cref="TNDrop.Core.BackupService"/> methods that mutate data
/// without going through <see cref="TNDrop.App.RunRestore"/> / <see cref="TNDrop.App.RunImport"/>
/// first for the two operations that replace the whole store (restore/import) -- those two static
/// entry points own pausing capture and reloading settings afterward (design doc §5 steps 4-5).
/// Create/delete/export talk to <see cref="TNDrop.App.Backup"/> directly since they do not touch
/// the live item store.</para>
/// </summary>
public sealed partial class BackupDialog : Window
{
    private sealed record BackupListItem(BackupEntry Entry, string Display);

    public BackupDialog()
    {
        InitializeComponent();

        Title = Strings.BackupDialogTitle;
        ListLabelText.Text = Strings.BackupListLabel;
        ManualButton.Content = Strings.BackupManualButton;
        RestoreButton.Content = Strings.BackupRestoreButton;
        DeleteButton.Content = Strings.BackupDeleteButton;
        MigrationLabelText.Text = Strings.BackupMigrationLabel;
        ExportButton.Content = Strings.BackupExportButton;
        ImportButton.Content = Strings.BackupImportButton;

        BackupList.SelectionChanged += (_, _) => UpdateButtonStates();

        RefreshList();
    }

    // ---- List -----------------------------------------------------------------------------

    private void RefreshList()
    {
        var entries = global::TNDrop.App.Backup?.ListBackups() ?? Array.Empty<BackupEntry>();
        var items = entries
            .Select(e => new BackupListItem(e, $"{KindLabel(e.Kind)} {e.CreatedLocal:yyyy/MM/dd HH:mm:ss}"))
            .ToList();

        BackupList.ItemsSource = items;
        BackupList.DisplayMemberPath = nameof(BackupListItem.Display);

        AutoHintText.Text = global::TNDrop.App.Settings.AutoBackupEnabled
            ? Strings.BackupAutoHintOn
            : Strings.BackupAutoHintOff;

        UpdateButtonStates();
    }

    private void UpdateButtonStates()
    {
        var hasSelection = BackupList.SelectedItem is not null;
        RestoreButton.IsEnabled = hasSelection;
        DeleteButton.IsEnabled = hasSelection;
    }

    private static string KindLabel(BackupKind kind) => kind switch
    {
        BackupKind.Auto => Strings.BackupKindAuto,
        BackupKind.Manual => Strings.BackupKindManual,
        _ => Strings.BackupKindSafety,
    };

    // ---- Manual backup ----------------------------------------------------------------------

    private void OnManualClick(object sender, RoutedEventArgs e)
    {
        var path = global::TNDrop.App.Backup?.CreateBackup(BackupKind.Manual);
        if (path is null)
        {
            ShowMessage(Strings.BackupManualFailed, MessageBoxImage.Error);
        }

        RefreshList();
    }

    // ---- Restore ----------------------------------------------------------------------------

    private void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not BackupListItem selected)
        {
            return;
        }

        var validation = global::TNDrop.App.Backup?.Validate(selected.Entry.FilePath) ?? BackupValidation.NotABackup;
        if (validation == BackupValidation.WrongEnvironment)
        {
            ShowMessage(Strings.BackupWrongEnvironment, MessageBoxImage.Warning);
            return;
        }

        if (validation == BackupValidation.NotABackup)
        {
            ShowMessage(Strings.BackupNotABackup, MessageBoxImage.Warning);
            return;
        }

        if (!ConfirmMessage(Strings.BackupRestoreConfirm))
        {
            return;
        }

        try
        {
            if (global::TNDrop.App.RunRestore(selected.Entry.FilePath))
            {
                ShowMessage(Strings.BackupRestoreDone, MessageBoxImage.Information);
            }
            else
            {
                // RunRestore's own contract says it returns false only when App.Backup/Monitor
                // have not been constructed yet -- not reachable from this dialog in practice (it
                // can only be open once OnStartup has gotten that far), but stay defensive rather
                // than silently claiming success. No dedicated string for this case; reuse the
                // closest generic "operation failed, check the log" sentence in the table.
                FileLogger.Instance?.Warn("backup", "RunRestore returned false unexpectedly");
                ShowMessage(Strings.BackupManualFailed, MessageBoxImage.Error);
            }
        }
        catch (BackupRestoreException ex)
        {
            ShowRestoreFailure(ex);
        }
        finally
        {
            RefreshList();
        }
    }

    // ---- Delete -----------------------------------------------------------------------------

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not BackupListItem selected)
        {
            return;
        }

        if (!ConfirmMessage(Strings.BackupDeleteConfirm))
        {
            return;
        }

        try
        {
            global::TNDrop.App.Backup?.DeleteBackup(selected.Entry.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // BackupService.DeleteBackup deliberately does not swallow its own failures (see its
            // doc comment) -- the caller must surface something rather than silently no-op. No
            // dedicated delete-failure string in the table; reuse the closest generic one.
            FileLogger.Instance?.Warn("backup", $"failed to delete backup: {ex.GetType().Name}");
            ShowMessage(Strings.BackupManualFailed, MessageBoxImage.Error);
        }
        finally
        {
            RefreshList();
        }
    }

    // ---- Export -----------------------------------------------------------------------------

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        var pwDialog = PasswordDialog.ForExport();
        pwDialog.Owner = this;
        if (pwDialog.ShowDialog() != true)
        {
            return;
        }

        var saveDialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = Strings.ExportFileFilter,
            FileName = $"TNDrop-export-{DateTime.Now:yyyyMMdd}.tndexport",
        };
        if (saveDialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            global::TNDrop.App.Backup?.ExportTo(saveDialog.FileName, pwDialog.Password);
            ShowMessage(
                string.Format(CultureInfo.CurrentUICulture, Strings.ExportDoneFormat, saveDialog.FileName),
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            // Covers IOException (destination locked/missing directory, etc. -- Task 5 carry-over:
            // ExportTo's File.WriteAllBytes(destPath, ...) is not wrapped by BackupService) as well
            // as any other export failure; all map to the same generic ExportFailed sentence.
            FileLogger.Instance?.Warn("backup", $"export failed: {ex.GetType().Name}");
            ShowMessage(Strings.ExportFailed, MessageBoxImage.Error);
        }
    }

    // ---- Import -----------------------------------------------------------------------------

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        var openDialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Strings.ExportFileFilter,
        };
        if (openDialog.ShowDialog(this) != true)
        {
            return;
        }

        var pwDialog = PasswordDialog.ForImport();
        pwDialog.Owner = this;
        if (pwDialog.ShowDialog() != true)
        {
            return;
        }

        if (!ConfirmMessage(Strings.BackupRestoreConfirm))
        {
            return;
        }

        try
        {
            if (global::TNDrop.App.RunImport(openDialog.FileName, pwDialog.Password))
            {
                ShowMessage(Strings.BackupRestoreDone, MessageBoxImage.Information);
            }
            else
            {
                // See the matching comment in OnRestoreClick: not reachable from this dialog in
                // practice, kept defensive.
                FileLogger.Instance?.Warn("backup", "RunImport returned false unexpectedly");
                ShowMessage(Strings.ImportWrongPassword, MessageBoxImage.Error);
            }
        }
        catch (ExportPasswordException)
        {
            ShowMessage(Strings.ImportWrongPassword, MessageBoxImage.Error);
        }
        catch (ExportFormatException)
        {
            ShowMessage(Strings.ImportNotExportFile, MessageBoxImage.Error);
        }
        catch (BackupRestoreException ex)
        {
            ShowRestoreFailure(ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Task 5 carry-over: ImportFrom's File.ReadAllBytes(srcPath) can throw a raw
            // IOException (missing/locked source file) or UnauthorizedAccessException
            // (permission denied) before any password/format check runs. Closest existing
            // string per the brief: the generic "wrong password or corrupted file" sentence --
            // from the user's point of view the file could not be read either way.
            FileLogger.Instance?.Warn("backup", $"import failed: {ex.GetType().Name}");
            ShowMessage(Strings.ImportWrongPassword, MessageBoxImage.Error);
        }
        finally
        {
            RefreshList();
        }
    }

    // ---- Shared helpers ---------------------------------------------------------------------

    private void ShowRestoreFailure(BackupRestoreException ex)
    {
        var message = ex.RolledBack
            ? Strings.BackupRestoreFailedRolledBack
            : string.Format(
                CultureInfo.CurrentUICulture,
                Strings.BackupRestoreFailedFatalFormat,
                global::TNDrop.App.Backup?.BackupsDir ?? "");

        ShowMessage(message, MessageBoxImage.Error);
    }

    private void ShowMessage(string text, MessageBoxImage icon) =>
        MessageBox.Show(this, text, Strings.BackupDialogTitle, MessageBoxButton.OK, icon);

    private bool ConfirmMessage(string text) =>
        MessageBox.Show(this, text, Strings.BackupDialogTitle, MessageBoxButton.OKCancel, MessageBoxImage.Warning)
        == MessageBoxResult.OK;
}
