using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TNDrop.Core;
using TNDrop.Platform;
using TNDrop.Resources;
using TNDrop.Services;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;

namespace TNDrop.UI;

/// <summary>
/// The clipboard shelf: a panel that slides in from the configured screen edge and retracts a
/// moment after the pointer leaves. Content is a placeholder at this stage; only the geometry,
/// the slide animation and the retract timing are real.
/// </summary>
public partial class ShelfWindow : Window
{
    private const string Module = "ShelfWindow";

    /// <summary>Placement passes allowed per ApplySettings call. See the re-entrancy latch there.</summary>
    private const int MaxPlacementPasses = 2;

    private static readonly Duration SlideInDuration = new(TimeSpan.FromMilliseconds(250));
    private static readonly Duration SlideOutDuration = new(TimeSpan.FromMilliseconds(180));

    private static readonly Brush ActiveTabBackground = Freeze(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42)));
    private static readonly Brush ActiveTabForeground = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF7)));
    private static readonly Brush TabBadgeBackground = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));

    private readonly DispatcherTimer _retractTimer;

    private AppSettings? _settings;
    private EdgeSide _edge = EdgeSide.Left;
    private double _shownX;
    private double _hiddenX = -ShelfPlacement.ShelfWidth;
    private MonitorGeometry.WorkArea _area;
    private ShelfPlacement.Rect _rect;
    private bool _placed;
    private bool _pointerInside;
    private bool _slidingOut;
    private bool _applying;
    private bool _reapplyRequested;

    private ItemStore? _itemStore;
    private ShelfViewModel? _shelfViewModel;
    private (Button Button, CardFilter Filter, string Label, Func<int> Count)[] _filterTabs =
        Array.Empty<(Button, CardFilter, string, Func<int>)>();

    private ScrollViewer? _cardsScrollViewer;
    private double _savedCardsScrollOffset = -1;

    public ShelfWindow()
    {
        InitializeComponent();

        _retractTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _retractTimer.Tick += OnRetractTick;

        MouseEnter += OnPointerEnter;
        MouseLeave += OnPointerLeave;
        IsVisibleChanged += OnSelfVisibleChanged;
        DpiChanged += OnDpiChanged;

        // Create the HWND now. ApplySettings runs before the shelf is ever shown and its
        // device-pixel snap needs a handle; without this the first placement silently skips it.
        new WindowInteropHelper(this).EnsureHandle();

        InitializeCardList();
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// True while the pointer is over the shelf, or while keyboard focus is inside it (typing in
    /// the search box). Drives the retract timer -- without the keyboard-focus half, a user who
    /// clicks into the search box and then types without the mouse moving would get retracted out
    /// from under them mid-sentence once the existing hover countdown elapses.
    /// </summary>
    public bool IsPointerInside => _pointerInside || IsMouseOver || IsKeyboardFocusWithin;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Must run after the HWND exists. WS_EX_NOACTIVATE is what keeps the shelf from stealing
        // focus from whatever the user is typing in when it slides in.
        WindowStyles.MakeToolWindowNoActivate(this);
    }

    /// <summary>Recomputes geometry and retract timing from the settings, resolving monitor and DPI.</summary>
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
            FileLogger.Instance?.Error(Module, "Failed to place the shelf", ex);
        }
        finally
        {
            _reapplyRequested = false;
            _applying = false;
        }
    }

    private void Place(AppSettings s)
    {
        _edge = s.Edge;

        var area = MonitorGeometry.Resolve(s.MonitorDeviceName, this);
        var rect = ShelfPlacement.ShelfRect(new ShelfPlacement.Rect(area.X, area.Y, area.W, area.H), _edge);

        _area = area;
        _rect = rect;
        _placed = true;
        _shownX = rect.X;
        _hiddenX = ShelfPlacement.HiddenX(rect, _edge);

        Panel.CornerRadius = _edge == EdgeSide.Left
            ? new CornerRadius(0, 12, 12, 0)
            : new CornerRadius(12, 0, 0, 12);

        _retractTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(s.RetractDelayMs, 100, 10_000));

        // A settings change mid-slide-out finishes the retract rather than leaving the shelf
        // parked halfway with no timer running.
        var wasSlidingOut = _slidingOut;
        StopSlide();

        var showing = IsVisible && !wasSlidingOut;
        var x = showing ? _shownX : _hiddenX;

        Width = rect.W;
        Height = rect.H;
        Top = rect.Y;
        Left = x;

        if (!showing && IsVisible)
            Hide();

        SnapToDevicePixels(x);

        // StopSlide above killed any in-flight slide-in, so its Completed handler will not run to
        // arm the countdown. Without this the shelf would sit out with no timer.
        if (showing)
            ArmRetractIfPointerOutside();

        FileLogger.Instance?.Info(Module,
            $"placed on {area.DeviceName} scale {area.ScaleX:0.##}: shown X {_shownX:0}, " +
            $"hidden X {_hiddenX:0}, {rect.W:0}x{rect.H:0} DIP, retract {_retractTimer.Interval.TotalMilliseconds:0} ms");
    }

    /// <summary>Slides the shelf in from off-screen with a slight overshoot (250 ms, BackEase EaseOut).</summary>
    public void SlideIn()
    {
        // Read the live value first: if a retract is in flight this is the halfway position, and
        // reversing from there is what makes an interrupted retract feel continuous.
        var from = IsVisible ? Left : _hiddenX;

        StopSlide();
        _retractTimer.Stop();

        if (!IsVisible)
        {
            Left = _hiddenX;
            Show();
        }

        // Base value first, animation second: with FillBehavior.Stop the property falls back to
        // the base value the instant the clock ends, so the base value must already be the target.
        Left = _shownX;

        var animation = new DoubleAnimation(from, _shownX, SlideInDuration)
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 },
            FillBehavior = FillBehavior.Stop,
        };
        animation.Completed += OnSlideInCompleted;

        BeginAnimation(LeftProperty, animation);
    }

    private void OnSlideInCompleted(object? sender, EventArgs e)
    {
        if (_slidingOut || !IsVisible)
            return;

        // Land on the exact device pixel. Measured on a 125% display: without this the shelf
        // came to rest 2px short of the screen edge every time, leaving a sliver of desktop
        // showing down the side. FillBehavior.Stop reverts Left to a base value it already
        // equals, so the last animated frame -- which never lands exactly on the target -- is
        // what the HWND keeps. Snapping is cheap and makes the resting position exact.
        SnapToDevicePixels(_shownX);

        // A pointer that flicks past the trigger band and never lands on the shelf produces no
        // MouseEnter and therefore no MouseLeave, so nothing else would ever start the countdown
        // -- and the trigger band is hidden while the shelf is out, so the user would have no way
        // to dismiss it. Arm it here instead of relying on the pointer arriving.
        ArmRetractIfPointerOutside();
    }

    /// <summary>Pins the window to the device-pixel rectangle that <paramref name="xDip"/> maps to on the target monitor.</summary>
    private void SnapToDevicePixels(double xDip)
    {
        if (!_placed)
            return;

        MonitorGeometry.SnapToDeviceRect(this,
            xDip * _area.ScaleX, _rect.Y * _area.ScaleY,
            _rect.W * _area.ScaleX, _rect.H * _area.ScaleY);
    }

    /// <summary>Slides the shelf back off-screen (180 ms, QuadraticEase EaseIn) and hides it.</summary>
    public void SlideOut()
    {
        if (!IsVisible)
            return;

        var from = Left;

        StopSlide();
        _retractTimer.Stop();
        _slidingOut = true;

        Left = _hiddenX;

        var animation = new DoubleAnimation(from, _hiddenX, SlideOutDuration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop,
        };
        animation.Completed += OnSlideOutCompleted;

        BeginAnimation(LeftProperty, animation);
    }

    private void OnSlideOutCompleted(object? sender, EventArgs e)
    {
        // Cleared by a SlideIn that interrupted this retract; the shelf must stay visible.
        if (!_slidingOut)
            return;

        _slidingOut = false;
        BeginAnimation(LeftProperty, null);
        Left = _hiddenX;
        SnapToDevicePixels(_hiddenX);
        Hide();
    }

    private void StopSlide()
    {
        _slidingOut = false;
        BeginAnimation(LeftProperty, null);
    }

    private void OnPointerEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _pointerInside = true;
        _retractTimer.Stop();
    }

    private void OnPointerLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _pointerInside = false;
        ArmRetractIfPointerOutside();
    }

    private void OnSelfVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
            return;

        // A hidden window never gets a MouseLeave. Leaving the flag set would make
        // IsPointerInside permanently true and suppress every future retract -- the shelf would
        // open once more and then never close again.
        _pointerInside = false;
        _retractTimer.Stop();

        // Whatever temporarily allowed real activation for the search box (see
        // OnSearchBoxPreviewMouseLeftButtonDown) must not survive the shelf going away, or the
        // *next* slide-in would start pre-activated instead of NOACTIVATE. Idempotent if the
        // search box was never focused this time around.
        WindowStyles.SetNoActivate(this, true);
        Keyboard.ClearFocus();
    }

    /// <summary>
    /// Starts the retract countdown unless the pointer is on the shelf. Every path that leaves
    /// the shelf visible must call this: the shelf only ever hides itself on this timer, and the
    /// trigger band is hidden while the shelf is out, so a visible shelf with no timer running is
    /// a shelf the user cannot get rid of.
    /// </summary>
    private void ArmRetractIfPointerOutside()
    {
        _retractTimer.Stop();
        if (IsVisible && !IsPointerInside)
            _retractTimer.Start();
    }

    private void OnRetractTick(object? sender, EventArgs e)
    {
        if (IsPointerInside)
        {
            // Suppressed, not cancelled. Re-arm rather than drop the timer: if IsPointerInside is
            // wrong (or the MouseLeave that would normally re-arm never arrives), dropping it here
            // is what wedges the shelf open permanently.
            _retractTimer.Stop();
            _retractTimer.Start();
            return;
        }

        _retractTimer.Stop();
        SlideOut();
    }

    private void OnDpiChanged(object sender, System.Windows.DpiChangedEventArgs e)
    {
        if (_settings is not null)
            ApplySettings(_settings);
    }

    /// <summary>
    /// Wires the card list up to <see cref="TNDrop.App.Store"/>. Guarded against a null store so
    /// that constructing a ShelfWindow never crashes outside the normal App.OnStartup sequence
    /// (e.g. the XAML designer, which never runs App.OnStartup).
    /// </summary>
    private void InitializeCardList()
    {
        var store = global::TNDrop.App.Store;
        if (store is null)
        {
            return;
        }

        _itemStore = store;
        _shelfViewModel = new ShelfViewModel(store);
        DataContext = _shelfViewModel;

        _filterTabs = new (Button, CardFilter, string, Func<int>)[]
        {
            (FilterAllButton, CardFilter.All, Strings.FilterAll, () => _shelfViewModel.CountAll),
            (FilterTextButton, CardFilter.Text, Strings.FilterText, () => _shelfViewModel.CountText),
            (FilterLinksButton, CardFilter.Links, Strings.FilterLinks, () => _shelfViewModel.CountLinks),
            (FilterImagesButton, CardFilter.Images, Strings.FilterImages, () => _shelfViewModel.CountImages),
            (FilterFilesButton, CardFilter.Files, Strings.FilterFiles, () => _shelfViewModel.CountFiles),
        };

        foreach (var tab in _filterTabs)
        {
            var filter = tab.Filter;
            tab.Button.Click += (_, _) => SetFilter(filter);
        }

        SearchPlaceholderText.Text = Strings.SearchPlaceholder;
        SearchBox.TextChanged += OnSearchTextChanged;
        OnSearchTextChanged(SearchBox, null!);

        // The shelf never activates on its own (WS_EX_NOACTIVATE, see OnSourceInitialized) so
        // typing normally goes to whichever app was focused before the shelf slid in. Clicking
        // the search box is the one deliberate exception the design calls for: grant real
        // activation just long enough to type a query, then revoke it once focus leaves.
        SearchBox.PreviewMouseLeftButtonDown += OnSearchBoxPreviewMouseLeftButtonDown;
        SearchBox.LostKeyboardFocus += OnSearchBoxLostKeyboardFocus;

        ClearButton.Content = Strings.ClearButton;
        ClearButton.Click += OnClearButtonClick;

        CardsList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnCardActionClick));
        PinnedList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnCardActionClick));

        _shelfViewModel.PinnedCards.CollectionChanged += (_, _) => UpdatePinnedVisibility();
        _shelfViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null || e.PropertyName.StartsWith("Count", StringComparison.Ordinal))
            {
                UpdateFilterTabs();
            }
        };

        // Store-driven rebuilds (a background clipboard capture, a pin toggle from elsewhere,
        // etc.) Clear()+repopulate Cards, which resets the ListBox's scroll to the top. Save/
        // restore the offset around exactly those rebuilds -- not around Filter/SearchText
        // changes, where jumping to the top is the expected, user-initiated behavior.
        _shelfViewModel.StoreRebuilding += OnStoreRebuilding;
        _shelfViewModel.StoreRebuilt += OnStoreRebuilt;

        UpdateFilterTabs();
        UpdatePinnedVisibility();
    }

    private void SetFilter(CardFilter filter)
    {
        if (_shelfViewModel is null)
        {
            return;
        }

        _shelfViewModel.Filter = filter;
        UpdateFilterTabs();
    }

    private void UpdateFilterTabs()
    {
        if (_shelfViewModel is null)
        {
            return;
        }

        foreach (var tab in _filterTabs)
        {
            tab.Button.Content = BuildTabContent(tab.Label, tab.Count());

            if (tab.Filter == _shelfViewModel.Filter)
            {
                tab.Button.Background = ActiveTabBackground;
                tab.Button.Foreground = ActiveTabForeground;
            }
            else
            {
                tab.Button.ClearValue(BackgroundProperty);
                tab.Button.ClearValue(ForegroundProperty);
            }
        }
    }

    private static object BuildTabContent(string label, int count)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new Border
        {
            Background = TabBadgeBackground,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(5, 0, 0, 0),
            Child = new TextBlock
            {
                Text = count.ToString(CultureInfo.CurrentUICulture),
                FontSize = 10,
            },
        });
        return panel;
    }

    private void UpdatePinnedVisibility()
    {
        PinnedScroll.Visibility = _shelfViewModel is not null && _shelfViewModel.PinnedCards.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholderText.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// Class handler for every pin/delete button rendered inside a card (see Cards.xaml): both
    /// the pinned deck and the main card list route their Button.Click here instead of each
    /// button needing its own handler, since the buttons live inside a shared DataTemplate with
    /// no code-behind of its own.
    /// </summary>
    private void OnCardActionClick(object sender, RoutedEventArgs e)
    {
        if (_itemStore is null)
        {
            return;
        }

        if (e.OriginalSource is not Button button || button.DataContext is not CardViewModel card)
        {
            return;
        }

        switch (button.Tag as string)
        {
            case "Pin":
                _itemStore.SetPinned(card.Id, !card.Pinned);
                break;
            case "Delete":
                _itemStore.Remove(card.Id);
                break;
        }

        e.Handled = true;
    }

    private void OnClearButtonClick(object sender, RoutedEventArgs e)
    {
        if (_shelfViewModel is null)
        {
            return;
        }

        var count = _shelfViewModel.Cards.Count;
        if (count == 0)
        {
            return;
        }

        var message = string.Format(CultureInfo.CurrentUICulture, Strings.ClearConfirmMessageFormat, count);
        var result = MessageBox.Show(this, message, Strings.ClearConfirmTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            _shelfViewModel.ClearVisible();
        }
    }

    /// <summary>
    /// Grants the search box a deliberate, temporary exception to "the shelf never steals focus":
    /// strip WS_EX_NOACTIVATE, force real activation, then move WPF keyboard focus into the box.
    /// WS_EX_NOACTIVATE only suppresses activation as a *side effect of this click* (the
    /// WM_MOUSEACTIVATE decision for it is already made by the time this handler runs) -- the
    /// explicit SetForegroundWindow call is what actually makes the window (and therefore the
    /// search box) receive real keyboard input afterward.
    /// </summary>
    private void OnSearchBoxPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        WindowStyles.SetNoActivate(this, false);
        WindowStyles.BringToForeground(this);
        Keyboard.Focus(SearchBox);
    }

    /// <summary>
    /// Revokes the temporary activation exception once the user is done with the search box
    /// (clicked elsewhere, tabbed away, or the shelf itself is going away). Re-arms the retract
    /// countdown too: while focus was in the box, IsPointerInside was held true by
    /// IsKeyboardFocusWithin even if the mouse had drifted off the shelf.
    /// </summary>
    private void OnSearchBoxLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        WindowStyles.SetNoActivate(this, true);
        ArmRetractIfPointerOutside();
    }

    private void OnStoreRebuilding()
    {
        var sv = GetCardsScrollViewer();
        _savedCardsScrollOffset = sv?.VerticalOffset ?? -1;
    }

    private void OnStoreRebuilt()
    {
        if (_savedCardsScrollOffset < 0)
        {
            return;
        }

        var offset = _savedCardsScrollOffset;
        _savedCardsScrollOffset = -1;

        // Deferred to Loaded priority: Cards was just Clear()-and-repopulated, and
        // ScrollableHeight/ExtentHeight only reflect the new item count after the layout pass
        // that follows runs, not synchronously here.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var sv = GetCardsScrollViewer();
            if (sv is null)
            {
                return;
            }

            var clamped = Math.Max(0, Math.Min(offset, sv.ScrollableHeight));
            sv.ScrollToVerticalOffset(clamped);
        }), DispatcherPriority.Loaded);
    }

    private ScrollViewer? GetCardsScrollViewer() =>
        _cardsScrollViewer ??= FindVisualChild<ScrollViewer>(CardsList);

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
            {
                return typed;
            }

            var found = FindVisualChild<T>(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
