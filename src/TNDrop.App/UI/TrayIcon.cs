using System;
using System.Drawing;
using System.Windows.Forms;
using TNDrop.Core;
using TNDrop.Resources;

namespace TNDrop.UI;

/// <summary>
/// Notification-area icon and its right-click context menu. This is the only UI surface
/// the app shows before Task 9's clipboard window exists, and stays the app's entry
/// point for settings/about/exit afterwards.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _hoverItem;
    private readonly ToolStripMenuItem _incognitoItem;
    private readonly Icon? _ownedIcon;
    private bool _disposed;

    public event Action? OpenSettingsRequested;
    public event Action? ExitRequested;
    public event Action<bool>? HoverEnabledChanged;
    public event Action<bool>? IncognitoChanged;

    public TrayIcon()
    {
        // Click (not CheckOnClick+CheckedChanged) so that SetHoverEnabled/SetIncognito -- used
        // to sync the checkmark from settings at startup and from other UI later -- never
        // re-raise these events and trigger a redundant settings save.
        _hoverItem = new ToolStripMenuItem(Strings.TrayHover);
        _hoverItem.Click += (_, _) =>
        {
            _hoverItem.Checked = !_hoverItem.Checked;
            HoverEnabledChanged?.Invoke(_hoverItem.Checked);
        };

        _incognitoItem = new ToolStripMenuItem(Strings.TrayIncognito);
        _incognitoItem.Click += (_, _) =>
        {
            _incognitoItem.Checked = !_incognitoItem.Checked;
            IncognitoChanged?.Invoke(_incognitoItem.Checked);
        };

        var settingsItem = new ToolStripMenuItem(Strings.TraySettings);
        settingsItem.Click += (_, _) => OpenSettingsRequested?.Invoke();

        var aboutItem = new ToolStripMenuItem(Strings.TrayAbout);
        aboutItem.Click += (_, _) => ShowAbout();

        var exitItem = new ToolStripMenuItem(Strings.TrayExit);
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_hoverItem);
        menu.Items.Add(_incognitoItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(aboutItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _ownedIcon = TryExtractProcessIcon();

        _notifyIcon = new NotifyIcon
        {
            Icon = _ownedIcon ?? SystemIcons.Application,
            Text = Strings.AppName,
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    /// <summary>Syncs the checkable menu items to the current settings without re-raising the change events.</summary>
    public void SetHoverEnabled(bool value) => _hoverItem.Checked = value;

    public void SetIncognito(bool value)
    {
        _incognitoItem.Checked = value;

        // NotifyIcon.Text is capped at 63 chars on older shells; AppName + suffix is short
        // enough to stay well under that, so no truncation is needed here.
        _notifyIcon.Text = value ? Strings.AppName + Strings.TrayTooltipIncognitoSuffix : Strings.AppName;
    }

    public void ShowBalloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Warning)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(5000);
    }

    private static void ShowAbout()
    {
        var text = string.Format(Strings.AboutText, AppVersion.Display, Environment.NewLine);
        MessageBox.Show(text, Strings.TrayAbout, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static Icon? TryExtractProcessIcon()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            return processPath is null ? null : Icon.ExtractAssociatedIcon(processPath);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _ownedIcon?.Dispose();
    }
}
