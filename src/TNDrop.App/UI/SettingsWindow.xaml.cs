using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TNDrop.Core;
using TNDrop.Platform;
using TNDrop.Resources;
using TNDrop.Services;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;

namespace TNDrop.UI;

/// <summary>
/// The settings window: three categories (動作/位置/外観), immediate-apply, no OK/Cancel. Every
/// control here reads its initial value from <see cref="TNDrop.App.Settings"/> in the
/// constructor and, on every subsequent user change, calls straight into an
/// <c>App.Set*</c> method -- see App.xaml.cs's "Settings window entry points" region for what
/// each one does. This window never writes <c>App.Settings</c> or calls
/// <c>App.SaveSettings()</c> itself, so there is exactly one place that persists and propagates
/// any given setting, no matter whether the change came from here or (for autostart/incognito)
/// the tray menu.
/// </summary>
public partial class SettingsWindow : Window
{
    private const string Module = "SettingsWindow";

    private static readonly Brush ActiveNavBackground = Freeze(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42)));
    private static readonly Brush ActiveNavForeground = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF7)));

    /// <summary>
    /// True only while the constructor is still populating control values from
    /// <see cref="TNDrop.App.Settings"/>. Every change handler below checks this first: setting
    /// e.g. <c>IsChecked</c> or <c>Value</c> in the constructor raises the very same
    /// Checked/ValueChanged/SelectionChanged event a real user action would, and without this
    /// guard the window would call every App.Set* method (and so re-save settings.json, replay
    /// window placement, and flash the position preview) once for every control the instant it
    /// opens. Mirrors TrayIcon's own "SetHoverEnabled/SetIncognito never re-raise the change
    /// event" guard, which solves the identical problem for the tray's checkable menu items.
    /// </summary>
    private bool _initializing = true;

    private PositionPreviewWindow? _preview;

    private sealed record MonitorOption(string? DeviceName, string Display);
    private sealed record AutoDeleteOption(AutoDeletePolicy Value, string Display);

    public SettingsWindow()
    {
        InitializeComponent();

        Title = Strings.SettingsWindowTitle;

        var settings = global::TNDrop.App.Settings;

        BuildNav();
        BuildBehaviorPage(settings);
        BuildPositionPage(settings);
        BuildAppearancePage(settings);
        BuildFooter();

        SetCategory(0);

        // The preview overlay is its own top-level Window with its own DispatcherTimer; neither
        // is owned by anything that goes away when this window closes, so both would otherwise
        // leak for the rest of the process's life every time a settings session touched a
        // position field (see PositionPreviewWindow's own Closed handler for the timer half).
        Closed += (_, _) => _preview?.Close();

        _initializing = false;
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    // ---- Nav ---------------------------------------------------------------------------------

    private void BuildNav()
    {
        NavBehaviorButton.Content = Strings.SettingsNavBehavior;
        NavPositionButton.Content = Strings.SettingsNavPosition;
        NavAppearanceButton.Content = Strings.SettingsNavAppearance;

        NavBehaviorButton.Click += (_, _) => SetCategory(0);
        NavPositionButton.Click += (_, _) => SetCategory(1);
        NavAppearanceButton.Click += (_, _) => SetCategory(2);
    }

    /// <summary>
    /// Switches the visible category. The three ScrollViewers are never recreated -- only their
    /// Visibility toggles -- so each one's scroll offset is exactly where the user left it the
    /// next time they come back to it, with no explicit save/restore step needed.
    /// </summary>
    private void SetCategory(int index)
    {
        BehaviorScroll.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        PositionScroll.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        AppearanceScroll.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

        HighlightNav(NavBehaviorButton, index == 0);
        HighlightNav(NavPositionButton, index == 1);
        HighlightNav(NavAppearanceButton, index == 2);
    }

    private static void HighlightNav(Button button, bool active)
    {
        if (active)
        {
            button.Background = ActiveNavBackground;
            button.Foreground = ActiveNavForeground;
        }
        else
        {
            button.ClearValue(BackgroundProperty);
            button.ClearValue(ForegroundProperty);
        }
    }

    // ---- 動作 (Behavior) -----------------------------------------------------------------

    private void BuildBehaviorPage(AppSettings settings)
    {
        AutoStartCheckBox.Content = Strings.SettingsAutoStart;
        SoundsCheckBox.Content = Strings.SettingsSoundsEnabled;
        // Reuses the tray's own incognito label: this checkbox and the tray menu item are two
        // controls for the exact same setting, so they should say the exact same thing.
        IncognitoCheckBox.Content = Strings.TrayIncognito;
        MoveToTopCheckBox.Content = Strings.SettingsMoveToTopOnCopy;

        WireCheckBox(AutoStartCheckBox, settings.AutoStartEnabled, global::TNDrop.App.SetAutoStartEnabled);
        WireCheckBox(SoundsCheckBox, settings.SoundsEnabled, global::TNDrop.App.SetSoundsEnabled);
        WireCheckBox(IncognitoCheckBox, settings.IncognitoMode, global::TNDrop.App.SetIncognitoMode);
        WireCheckBox(MoveToTopCheckBox, settings.MoveToTopOnCopy, global::TNDrop.App.SetMoveToTopOnCopy);

        AutoDeleteLabelText.Text = Strings.SettingsAutoDelete;
        var autoDeleteOptions = new[]
        {
            new AutoDeleteOption(AutoDeletePolicy.Off, Strings.SettingsAutoDeleteOffOption),
            new AutoDeleteOption(AutoDeletePolicy.Hours1, Strings.SettingsAutoDeleteHours1Option),
            new AutoDeleteOption(AutoDeletePolicy.Hours6, Strings.SettingsAutoDeleteHours6Option),
            new AutoDeleteOption(AutoDeletePolicy.Hours24, Strings.SettingsAutoDeleteHours24Option),
            new AutoDeleteOption(AutoDeletePolicy.Days7, Strings.SettingsAutoDeleteDays7Option),
        };
        AutoDeleteCombo.ItemsSource = autoDeleteOptions;
        AutoDeleteCombo.DisplayMemberPath = nameof(AutoDeleteOption.Display);
        AutoDeleteCombo.SelectedValuePath = nameof(AutoDeleteOption.Value);
        AutoDeleteCombo.SelectedValue = settings.AutoDelete;
        AutoDeleteCombo.SelectionChanged += (_, _) =>
        {
            if (_initializing || AutoDeleteCombo.SelectedItem is not AutoDeleteOption option)
            {
                return;
            }

            global::TNDrop.App.SetAutoDelete(option.Value);
        };

        RetractDelayLabelText.Text = Strings.SettingsRetractDelay;
        RetractDelaySlider.Minimum = 300;
        RetractDelaySlider.Maximum = 2000;
        RetractDelaySlider.TickFrequency = 100;
        RetractDelaySlider.IsSnapToTickEnabled = true;
        var retractDelay = Math.Clamp(settings.RetractDelayMs, 300, 2000);
        RetractDelaySlider.Value = retractDelay;
        UpdateRetractDelayText(retractDelay);
        RetractDelaySlider.ValueChanged += (_, e) =>
        {
            var value = (int)Math.Round(e.NewValue);
            UpdateRetractDelayText(value);

            if (_initializing)
            {
                return;
            }

            global::TNDrop.App.SetRetractDelayMs(value);
        };
    }

    private void UpdateRetractDelayText(int ms) =>
        RetractDelayValueText.Text = string.Format(CultureInfo.CurrentUICulture, Strings.SettingsMillisecondsFormat, ms);

    /// <summary>
    /// Wires a CheckBox's initial value and both flip directions to one callback -- the shared
    /// shape every boolean setting in this window follows, so a settings-window checkbox can
    /// never end up wired to Checked but not Unchecked (or vice versa) by an editing slip.
    /// </summary>
    private void WireCheckBox(CheckBox box, bool initial, Action<bool> onChange)
    {
        box.IsChecked = initial;
        box.Checked += (_, _) =>
        {
            if (!_initializing)
            {
                onChange(true);
            }
        };
        box.Unchecked += (_, _) =>
        {
            if (!_initializing)
            {
                onChange(false);
            }
        };
    }

    // ---- 位置 (Position) -----------------------------------------------------------------

    private void BuildPositionPage(AppSettings settings)
    {
        EdgeLabelText.Text = Strings.SettingsEdge;
        EdgeLeftRadio.Content = Strings.SettingsEdgeLeft;
        EdgeRightRadio.Content = Strings.SettingsEdgeRight;
        EdgeLeftRadio.IsChecked = settings.Edge == EdgeSide.Left;
        EdgeRightRadio.IsChecked = settings.Edge == EdgeSide.Right;
        EdgeLeftRadio.Checked += (_, _) => OnEdgeChanged(EdgeSide.Left);
        EdgeRightRadio.Checked += (_, _) => OnEdgeChanged(EdgeSide.Right);

        MonitorLabelText.Text = Strings.SettingsMonitor;
        BuildMonitorCombo(settings);

        HotZoneLabelText.Text = Strings.SettingsHotZone;
        HotZone25Radio.Content = string.Format(CultureInfo.CurrentUICulture, Strings.SettingsPercentFormat, 25);
        HotZone40Radio.Content = string.Format(CultureInfo.CurrentUICulture, Strings.SettingsPercentFormat, 40);
        HotZone60Radio.Content = string.Format(CultureInfo.CurrentUICulture, Strings.SettingsPercentFormat, 60);
        HotZone25Radio.IsChecked = settings.HotZonePercent == 25;
        HotZone40Radio.IsChecked = settings.HotZonePercent == 40;
        HotZone60Radio.IsChecked = settings.HotZonePercent == 60;
        HotZone25Radio.Checked += (_, _) => OnHotZoneChanged(25);
        HotZone40Radio.Checked += (_, _) => OnHotZoneChanged(40);
        HotZone60Radio.Checked += (_, _) => OnHotZoneChanged(60);

        TriggerSensitivityLabelText.Text = Strings.SettingsTriggerSensitivity;
        TriggerSensitivitySlider.Minimum = 1;
        TriggerSensitivitySlider.Maximum = 7;
        TriggerSensitivitySlider.TickFrequency = 1;
        TriggerSensitivitySlider.IsSnapToTickEnabled = true;
        var proximity = Math.Clamp(settings.TriggerProximityPx, 1, 7);
        TriggerSensitivitySlider.Value = proximity;
        UpdateTriggerSensitivityText(proximity);
        TriggerSensitivitySlider.ValueChanged += (_, e) =>
        {
            var value = (int)Math.Round(e.NewValue);
            UpdateTriggerSensitivityText(value);

            if (_initializing)
            {
                return;
            }

            global::TNDrop.App.SetTriggerProximityPx(value);
            ShowPositionPreview();
        };

        TriggerAlignLabelText.Text = Strings.SettingsTriggerAlign;
        TriggerAlignTopRadio.Content = Strings.SettingsTriggerAlignTop;
        TriggerAlignCenterRadio.Content = Strings.SettingsTriggerAlignCenter;
        TriggerAlignBottomRadio.Content = Strings.SettingsTriggerAlignBottom;
        TriggerAlignTopRadio.IsChecked = settings.TriggerAlign == TriggerAlign.Top;
        TriggerAlignCenterRadio.IsChecked = settings.TriggerAlign == TriggerAlign.Center;
        TriggerAlignBottomRadio.IsChecked = settings.TriggerAlign == TriggerAlign.Bottom;
        TriggerAlignTopRadio.Checked += (_, _) => OnTriggerAlignChanged(TriggerAlign.Top);
        TriggerAlignCenterRadio.Checked += (_, _) => OnTriggerAlignChanged(TriggerAlign.Center);
        TriggerAlignBottomRadio.Checked += (_, _) => OnTriggerAlignChanged(TriggerAlign.Bottom);
    }

    private void BuildMonitorCombo(AppSettings settings)
    {
        var options = new List<MonitorOption> { new(null, Strings.SettingsMonitorAuto) };
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            options.Add(new MonitorOption(screen.DeviceName, string.Format(
                CultureInfo.CurrentUICulture, Strings.SettingsMonitorFormat,
                screen.DeviceName, screen.Bounds.Width, screen.Bounds.Height)));
        }

        MonitorCombo.ItemsSource = options;
        MonitorCombo.DisplayMemberPath = nameof(MonitorOption.Display);
        MonitorCombo.SelectedValuePath = nameof(MonitorOption.DeviceName);

        // Falls back to the first (Auto) entry if the configured device name is not in the
        // current AllScreens list -- same "unknown monitor falls back to primary" contract
        // MonitorGeometry.Resolve already applies at placement time, kept consistent here so the
        // combo never shows a blank selection for a monitor that has since been unplugged.
        MonitorCombo.SelectedValue = settings.MonitorDeviceName;
        if (MonitorCombo.SelectedItem is null)
        {
            MonitorCombo.SelectedIndex = 0;
        }

        MonitorCombo.SelectionChanged += (_, _) =>
        {
            if (_initializing || MonitorCombo.SelectedItem is not MonitorOption option)
            {
                return;
            }

            global::TNDrop.App.SetMonitorDeviceName(option.DeviceName);
            ShowPositionPreview();
        };
    }

    private void UpdateTriggerSensitivityText(int px) =>
        TriggerSensitivityValueText.Text = string.Format(CultureInfo.CurrentUICulture, Strings.SettingsPixelFormat, px);

    private void OnEdgeChanged(EdgeSide edge)
    {
        if (_initializing)
        {
            return;
        }

        global::TNDrop.App.SetEdge(edge);
        ShowPositionPreview();
    }

    private void OnHotZoneChanged(int percent)
    {
        if (_initializing)
        {
            return;
        }

        global::TNDrop.App.SetHotZonePercent(percent);
        ShowPositionPreview();
    }

    private void OnTriggerAlignChanged(TriggerAlign align)
    {
        if (_initializing)
        {
            return;
        }

        global::TNDrop.App.SetTriggerAlign(align);
        ShowPositionPreview();
    }

    /// <summary>
    /// Flashes a 1.75s rectangle at the trigger band's actual position/height on the configured
    /// monitor, widened just enough to stay visible (the real band can be as little as 1 DIP
    /// wide) -- the same geometry function (<see cref="ShelfPlacement.TriggerRect"/>) that
    /// EdgeTriggerWindow itself places the real, invisible hover band with, so the preview can
    /// never show a position the real band would not actually occupy.
    /// </summary>
    private void ShowPositionPreview()
    {
        try
        {
            var settings = global::TNDrop.App.Settings;
            var area = MonitorGeometry.Resolve(settings.MonitorDeviceName, this);
            var rect = ShelfPlacement.TriggerRect(
                new ShelfPlacement.Rect(area.X, area.Y, area.W, area.H),
                settings.Edge, settings.TriggerProximityPx, settings.HotZonePercent, settings.TriggerAlign);

            const double MinVisibleWidth = 16;
            var visible = rect with { W = Math.Max(rect.W, MinVisibleWidth) };
            if (settings.Edge == EdgeSide.Right)
            {
                // TriggerRect anchors a Right-edge band to its own (possibly widened-past-the-
                // real-width) right side; recompute X so the widened preview still hugs the
                // monitor's right edge instead of drifting left of it.
                visible = visible with { X = area.X + area.W - visible.W };
            }

            _preview ??= new PositionPreviewWindow();
            _preview.ShowAt(area, visible);
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Warn(Module, $"position preview failed: {ex.Message}");
        }
    }

    // ---- 外観 (Appearance) ---------------------------------------------------------------

    private void BuildAppearancePage(AppSettings settings)
    {
        TextScaleLabelText.Text = Strings.SettingsTextScale;
        TextScaleSmallRadio.Content = Strings.SettingsTextScaleSmall;
        TextScaleNormalRadio.Content = Strings.SettingsTextScaleNormal;
        TextScaleMediumRadio.Content = Strings.SettingsTextScaleMedium;
        TextScaleLargeRadio.Content = Strings.SettingsTextScaleLarge;
        TextScaleSmallRadio.IsChecked = settings.TextScale == TextScale.Small;
        TextScaleNormalRadio.IsChecked = settings.TextScale == TextScale.Normal;
        TextScaleMediumRadio.IsChecked = settings.TextScale == TextScale.Medium;
        TextScaleLargeRadio.IsChecked = settings.TextScale == TextScale.Large;
        TextScaleSmallRadio.Checked += (_, _) => OnTextScaleChanged(TextScale.Small);
        TextScaleNormalRadio.Checked += (_, _) => OnTextScaleChanged(TextScale.Normal);
        TextScaleMediumRadio.Checked += (_, _) => OnTextScaleChanged(TextScale.Medium);
        TextScaleLargeRadio.Checked += (_, _) => OnTextScaleChanged(TextScale.Large);

        IndicatorStyleLabelText.Text = Strings.SettingsIndicatorStyle;
        IndicatorBeaconRadio.Content = Strings.SettingsIndicatorStyleBeacon;
        IndicatorBarRadio.Content = Strings.SettingsIndicatorStyleBar;
        IndicatorPulseRadio.Content = Strings.SettingsIndicatorStylePulse;
        IndicatorCornerRadio.Content = Strings.SettingsIndicatorStyleCorner;
        IndicatorBeaconRadio.IsChecked = settings.IndicatorStyle == IndicatorStyle.Beacon;
        IndicatorBarRadio.IsChecked = settings.IndicatorStyle == IndicatorStyle.Bar;
        IndicatorPulseRadio.IsChecked = settings.IndicatorStyle == IndicatorStyle.Pulse;
        IndicatorCornerRadio.IsChecked = settings.IndicatorStyle == IndicatorStyle.Corner;
        IndicatorBeaconRadio.Checked += (_, _) => OnIndicatorStyleChanged(IndicatorStyle.Beacon);
        IndicatorBarRadio.Checked += (_, _) => OnIndicatorStyleChanged(IndicatorStyle.Bar);
        IndicatorPulseRadio.Checked += (_, _) => OnIndicatorStyleChanged(IndicatorStyle.Pulse);
        IndicatorCornerRadio.Checked += (_, _) => OnIndicatorStyleChanged(IndicatorStyle.Corner);

        LanguageLabelText.Text = Strings.SettingsLanguage;
        LanguageRestartNoteText.Text = Strings.SettingsLanguageRestartNote;
        LanguageJaRadio.Content = Strings.SettingsLanguageJa;
        LanguageEnRadio.Content = Strings.SettingsLanguageEn;

        // Anything other than exactly "en" falls back to the ja radio being the one checked --
        // mirrors ApplyUiCulture's own fallback (an unrecognized Language value falls back to
        // "ja", not to nothing). Without this, a corrupt/unknown persisted value left BOTH radios
        // unchecked, an unrepresentable state for a 2-option exclusive choice.
        var isJapanese = settings.Language != "en";
        LanguageJaRadio.IsChecked = isJapanese;
        LanguageEnRadio.IsChecked = !isJapanese;
        LanguageJaRadio.Checked += (_, _) => OnLanguageChanged("ja");
        LanguageEnRadio.Checked += (_, _) => OnLanguageChanged("en");

        EdgeHintCheckBox.Content = Strings.SettingsEdgeHint;
        WireCheckBox(EdgeHintCheckBox, settings.EdgeHintEnabled, global::TNDrop.App.SetEdgeHintEnabled);
    }

    private void OnTextScaleChanged(TextScale scale)
    {
        if (_initializing)
        {
            return;
        }

        global::TNDrop.App.SetTextScale(scale);
    }

    /// <summary>Applies the style, then plays the very same test flash a real capture would --
    /// letting the user see what they just picked without having to trigger a real capture.</summary>
    private void OnIndicatorStyleChanged(IndicatorStyle style)
    {
        if (_initializing)
        {
            return;
        }

        global::TNDrop.App.SetIndicatorStyle(style);
        global::TNDrop.App.Indicator?.Flash(style, global::TNDrop.App.Settings.Edge);
    }

    private void OnLanguageChanged(string language)
    {
        if (_initializing)
        {
            return;
        }

        global::TNDrop.App.SetLanguage(language);
    }

    // ---- Footer --------------------------------------------------------------------------

    private void BuildFooter()
    {
        // Reuses the exact same AboutText resource (and Environment.NewLine substitution) as
        // TrayIcon's "About" dialog -- one string is the version+copyright text everywhere it
        // appears, rather than a second literal that could drift from the first at the next
        // version bump.
        FooterText.Text = string.Format(Strings.AboutText, Environment.NewLine);
    }

    // ---- Position preview overlay ----------------------------------------------------------

    /// <summary>
    /// A borderless, click-through rectangle shown for 1.75s at the configured screen edge
    /// whenever a position-affecting control changes, so the user sees where the trigger band
    /// will actually sit without having to move the mouse there. Built in code with no XAML of
    /// its own: a single flat Border needs no layout, and IndicatorWindow already established
    /// the "temporary overlay window, kept simple" precedent for this kind of transient effect.
    /// One instance is reused across calls (see <see cref="ShowAt"/>) rather than recreated per
    /// change, the same "restart rather than queue" contract <see cref="IndicatorWindow.Flash"/>
    /// uses for the capture confirmation.
    /// </summary>
    private sealed class PositionPreviewWindow : Window
    {
        private static readonly TimeSpan PreviewDuration = TimeSpan.FromMilliseconds(1750);
        private readonly DispatcherTimer _hideTimer;
        private readonly Border _fill;

        public PositionPreviewWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            Background = System.Windows.Media.Brushes.Transparent;

            _fill = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x90, 0x0A, 0x84, 0xFF)),
                CornerRadius = new CornerRadius(4),
            };
            Content = _fill;

            _hideTimer = new DispatcherTimer { Interval = PreviewDuration };
            _hideTimer.Tick += (_, _) =>
            {
                _hideTimer.Stop();
                Hide();
            };

            // Stops the timer before Close() tears down the HWND: left running, its next Tick
            // would call Hide() on an already-closed Window, which throws
            // InvalidOperationException ("Cannot set Visibility ... after Window is closed").
            // SettingsWindow.Closed calls Close() on this instance -- see that handler.
            Closed += (_, _) => _hideTimer.Stop();

            // Create the HWND now: MakeClickThrough (from OnSourceInitialized) and the first
            // ShowAt need a handle, matching every other overlay window in this app.
            new WindowInteropHelper(this).EnsureHandle();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            WindowStyles.MakeClickThrough(this);
        }

        /// <summary>Places the window at <paramref name="rect"/> (DIPs, already resolved against
        /// <paramref name="area"/>'s monitor) and (re)starts the 1.75s auto-hide countdown.</summary>
        public void ShowAt(MonitorGeometry.WorkArea area, ShelfPlacement.Rect rect)
        {
            Width = rect.W;
            Height = rect.H;
            Top = rect.Y;
            Left = rect.X;

            MonitorGeometry.SnapToDeviceRect(this,
                rect.X * area.ScaleX, rect.Y * area.ScaleY,
                rect.W * area.ScaleX, rect.H * area.ScaleY);

            if (!IsVisible)
            {
                Show();
            }

            _hideTimer.Stop();
            _hideTimer.Start();
        }
    }
}
