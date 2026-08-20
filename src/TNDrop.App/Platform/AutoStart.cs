using System;
using Microsoft.Win32;
using TNDrop.Services;

namespace TNDrop.Platform;

/// <summary>
/// Per-user logon autostart via HKCU Run. Per-user only: no admin rights, no service,
/// nothing that survives uninstall of the current user's profile.
/// </summary>
public static class AutoStart
{
    private const string Module = "AutoStart";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TNDrop";

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                FileLogger.Instance?.Error(Module, $"could not open HKCU\\{RunKeyPath}");
                return;
            }

            if (enabled)
            {
                var path = Environment.ProcessPath;
                if (string.IsNullOrEmpty(path))
                {
                    FileLogger.Instance?.Error(Module, "Environment.ProcessPath is empty; autostart not set");
                    return;
                }

                // Quoted: the install path can contain spaces (Program Files).
                key.SetValue(ValueName, $"\"{path}\"", RegistryValueKind.String);
                FileLogger.Instance?.Info(Module, "autostart enabled");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                FileLogger.Instance?.Info(Module, "autostart disabled");
            }
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Error(Module, $"SetEnabled({enabled}) failed", ex);
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is not null;
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Error(Module, "IsEnabled failed", ex);
            return false;
        }
    }
}
