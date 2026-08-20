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

    /// <summary>
    /// The exact value SetEnabled(true) would write right now -- the quoted current process
    /// path -- or null when Environment.ProcessPath is unavailable. One computation feeds both
    /// SetEnabled and a caller's drift check (see App.OnStartup's self-heal), so "what counts as
    /// correct" can never quietly disagree between the two: deriving the expected quoted path a
    /// second time somewhere else is exactly how a self-heal check would end up comparing against
    /// its own stale idea of what SetEnabled writes.
    /// </summary>
    public static string? ExpectedCommand()
    {
        var path = Environment.ProcessPath;

        // Quoted: the install path can contain spaces (Program Files).
        return string.IsNullOrEmpty(path) ? null : $"\"{path}\"";
    }

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
                var command = ExpectedCommand();
                if (command is null)
                {
                    FileLogger.Instance?.Error(Module, "Environment.ProcessPath is empty; autostart not set");
                    return;
                }

                key.SetValue(ValueName, command, RegistryValueKind.String);
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

    /// <summary>
    /// The raw string currently stored under the Run value, or null when it is absent (or on a
    /// registry read failure). Exists because <see cref="IsEnabled"/> only answers "is some value
    /// present", which reads a stale path (exe moved: reinstall, drive letter change) as
    /// indistinguishable from a correct one. A caller that needs to detect that drift -- not just
    /// presence -- compares this against <see cref="ExpectedCommand"/> instead.
    /// </summary>
    public static string? GetStoredCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Error(Module, "GetStoredCommand failed", ex);
            return null;
        }
    }
}
