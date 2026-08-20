using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using TNDrop.Services;

namespace TNDrop.Platform;

/// <summary>
/// Extended-window-style tweaks that WPF does not expose: keep a window out of Alt+Tab and
/// out of the taskbar, and stop it from stealing focus from the app the user is working in.
/// </summary>
public static class WindowStyles
{
    private const string Module = "WindowStyles";
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    // The Ptr variants are required on x64; this app ships win-x64 only.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    /// <summary>
    /// Adds WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE to the window. Safe to call before the
    /// window is shown: the HWND is created on demand. Failures are logged, never thrown.
    /// </summary>
    public static void MakeToolWindowNoActivate(System.Windows.Window w)
    {
        if (w is null)
            return;

        try
        {
            var helper = new WindowInteropHelper(w);
            var hwnd = helper.Handle;
            if (hwnd == IntPtr.Zero)
                hwnd = helper.EnsureHandle();

            if (hwnd == IntPtr.Zero)
            {
                FileLogger.Instance?.Error(Module, "window has no HWND; extended styles not applied");
                return;
            }

            Apply(hwnd);
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Error(Module, "MakeToolWindowNoActivate failed", ex);
        }
    }

    private static void Apply(IntPtr hwnd)
    {
        Marshal.SetLastSystemError(0);
        var current = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        if (current == 0)
        {
            var err = Marshal.GetLastWin32Error();
            if (err != 0)
            {
                FileLogger.Instance?.Error(Module, $"GetWindowLongPtr failed (Win32 error {err})");
                return;
            }
        }

        var updated = current | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        if (updated == current)
            return;

        Marshal.SetLastSystemError(0);
        var previous = SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(updated));
        if (previous == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            if (err != 0)
                FileLogger.Instance?.Error(Module, $"SetWindowLongPtr failed (Win32 error {err})");
        }
    }
}
