using System;
using System.Windows;
using System.Windows.Interop;
using TNDrop.Core;
using TNDrop.Platform;
using TNDrop.Services;

namespace TNDrop.UI;

/// <summary>
/// The invisible hover band pinned to the configured screen edge. Its only job is to notice the
/// pointer arriving and raise <see cref="Triggered"/>; the shelf itself is a separate window so
/// that showing the shelf never resizes or moves the thing the user is pointing at.
/// </summary>
public partial class EdgeTriggerWindow : Window
{
    private const string Module = "EdgeTriggerWindow";

    /// <summary>Placement passes allowed per ApplySettings call. See the re-entrancy latch there.</summary>
    private const int MaxPlacementPasses = 2;

    private AppSettings? _settings;
    private bool _applying;
    private bool _reapplyRequested;

    /// <summary>Raised when the pointer enters the band.</summary>
    public event Action? Triggered;

    public EdgeTriggerWindow()
    {
        InitializeComponent();

        MouseEnter += (_, _) => Triggered?.Invoke();
        DpiChanged += OnDpiChanged;

        // Create the HWND now. ApplySettings runs before the window is ever shown and its
        // device-pixel snap needs a handle; without this the first placement silently skips it.
        new WindowInteropHelper(this).EnsureHandle();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Must run after the HWND exists: keeps the band out of Alt+Tab and stops it taking focus.
        WindowStyles.MakeToolWindowNoActivate(this);
    }

    /// <summary>Recomputes size and position from the settings, resolving the target monitor and its DPI.</summary>
    public void ApplySettings(AppSettings s)
    {
        if (s is null)
            return;

        _settings = s;

        if (_applying)
        {
            // WM_DPICHANGED is delivered synchronously from inside Place's SetWindowPos call, so
            // Place can re-enter ApplySettings through DpiChanged. Dropping that re-entrant call
            // would leave the rect the OS suggested for the DPI change in place instead of ours.
            // Latch it and re-run once the outer pass unwinds.
            _reapplyRequested = true;
            FileLogger.Instance?.Info(Module, "placement re-entered during placement; will re-apply");
            return;
        }

        _applying = true;
        try
        {
            var passes = 0;
            do
            {
                _reapplyRequested = false;
                Place(_settings);
            }
            while (_reapplyRequested && ++passes < MaxPlacementPasses);
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Error(Module, "Failed to place the edge trigger band", ex);
        }
        finally
        {
            _reapplyRequested = false;
            _applying = false;
        }
    }

    private void Place(AppSettings s)
    {
        var area = MonitorGeometry.Resolve(s.MonitorDeviceName, this);
        var rect = ShelfPlacement.TriggerRect(
            new ShelfPlacement.Rect(area.X, area.Y, area.W, area.H),
            s.Edge, s.TriggerProximityPx, s.HotZonePercent, s.TriggerAlign);

        Width = rect.W;
        Height = rect.H;
        Left = rect.X;
        Top = rect.Y;

        HintBeacon.HorizontalAlignment = s.Edge == EdgeSide.Left
            ? System.Windows.HorizontalAlignment.Left
            : System.Windows.HorizontalAlignment.Right;

        MonitorGeometry.SnapToDeviceRect(this,
            rect.X * area.ScaleX, rect.Y * area.ScaleY,
            rect.W * area.ScaleX, rect.H * area.ScaleY);

        FileLogger.Instance?.Info(Module,
            $"placed on {area.DeviceName} scale {area.ScaleX:0.##} at " +
            $"({rect.X:0},{rect.Y:0}) {rect.W:0}x{rect.H:0} DIP");
    }

    /// <summary>Shows or hides the faint 2px beacon that hints where the shelf lives.</summary>
    public void SetHintVisible(bool visible)
        => HintBeacon.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private void OnDpiChanged(object sender, System.Windows.DpiChangedEventArgs e)
    {
        if (_settings is not null)
            ApplySettings(_settings);
    }
}
