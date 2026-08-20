using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using TNDrop.Services;

namespace TNDrop.Platform;

/// <summary>
/// Resolves the monitor a window should live on and hands back its work area already converted
/// to WPF device-independent pixels.
///
/// Monitor, DPI scale and work area are returned from a single call on purpose: every consumer
/// needs all three and they must agree with each other. Resolving them separately is how a
/// window ends up sized for one monitor and positioned for another.
/// </summary>
public static class MonitorGeometry
{
    private const string Module = "MonitorGeometry";

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;

    /// <summary>Work area of the resolved monitor in DIPs, plus the scale used to get there.</summary>
    public readonly record struct WorkArea(
        double X, double Y, double W, double H,
        double ScaleX, double ScaleY, string DeviceName);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>
    /// Finds the monitor named <paramref name="deviceName"/> (falling back to the primary one) and
    /// returns its work area in DIPs. <paramref name="dpiFallbackSource"/> is only consulted if the
    /// per-monitor DPI query fails.
    /// </summary>
    public static WorkArea Resolve(string? deviceName, Visual? dpiFallbackSource)
    {
        var screen = FindScreen(deviceName);
        var (scaleX, scaleY) = DpiScaleFor(screen, dpiFallbackSource);
        var wa = screen.WorkingArea; // device pixels

        return new WorkArea(
            wa.X / scaleX, wa.Y / scaleY, wa.Width / scaleX, wa.Height / scaleY,
            scaleX, scaleY, screen.DeviceName);
    }

    /// <summary>
    /// Snaps an already-created window to an exact device-pixel rectangle.
    ///
    /// Why this exists: WPF converts <see cref="Window.Left"/>/<see cref="Window.Top"/> to screen
    /// pixels using the DPI of the monitor the window is *currently* on. When the target monitor
    /// has a different scale, the DIP values computed here would land the window in the wrong
    /// place and it would never reach the target monitor to be corrected. Moving it once in raw
    /// device pixels breaks that circle; Windows then delivers WM_DPICHANGED and WPF's own
    /// Left/Top/transform catch up. No-op when the window has no HWND yet.
    /// </summary>
    public static void SnapToDeviceRect(Window window, double x, double y, double w, double h)
    {
        if (window is null)
            return;

        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            var cx = Math.Max(1, (int)Math.Round(w));
            var cy = Math.Max(1, (int)Math.Round(h));

            if (!SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(x), (int)Math.Round(y), cx, cy,
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER))
            {
                FileLogger.Instance?.Warn(Module,
                    $"SetWindowPos failed (Win32 error {Marshal.GetLastWin32Error()})");
            }
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Warn(Module, $"SnapToDeviceRect failed: {ex.Message}");
        }
    }

    private static System.Windows.Forms.Screen FindScreen(string? deviceName)
    {
        var all = System.Windows.Forms.Screen.AllScreens;

        if (!string.IsNullOrEmpty(deviceName))
        {
            var match = all.FirstOrDefault(s => string.Equals(s.DeviceName, deviceName, StringComparison.Ordinal));
            if (match is not null)
                return match;

            FileLogger.Instance?.Warn(Module,
                $"configured monitor was not found; falling back to the primary monitor");
        }

        var primary = System.Windows.Forms.Screen.PrimaryScreen;
        if (primary is not null)
            return primary;

        if (all.Length > 0)
            return all[0];

        throw new InvalidOperationException("No display device is available.");
    }

    private static (double ScaleX, double ScaleY) DpiScaleFor(System.Windows.Forms.Screen screen, Visual? fallback)
    {
        // Preferred: ask the target monitor directly, so the scale is right even when the window
        // is still sitting on a differently-scaled monitor.
        try
        {
            var b = screen.Bounds;
            var center = new POINT { X = b.Left + (b.Width / 2), Y = b.Top + (b.Height / 2) };
            var hmon = MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST);
            if (hmon != IntPtr.Zero &&
                GetDpiForMonitor(hmon, MDT_EFFECTIVE_DPI, out var dpiX, out var dpiY) == 0 &&
                dpiX > 0 && dpiY > 0)
            {
                return (dpiX / 96.0, dpiY / 96.0);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            FileLogger.Instance?.Warn(Module, $"GetDpiForMonitor unavailable: {ex.Message}");
        }

        // Fallback: the DPI WPF currently uses for this visual.
        if (fallback is not null)
        {
            try
            {
                var dpi = VisualTreeHelper.GetDpi(fallback);
                if (dpi.DpiScaleX > 0 && dpi.DpiScaleY > 0)
                    return (dpi.DpiScaleX, dpi.DpiScaleY);
            }
            catch (Exception ex)
            {
                FileLogger.Instance?.Warn(Module, $"VisualTreeHelper.GetDpi failed: {ex.Message}");
            }
        }

        return (1.0, 1.0);
    }
}
