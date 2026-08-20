using System;
using System.Diagnostics;
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
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using MessageBox = System.Windows.MessageBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseEventHandler = System.Windows.Input.MouseEventHandler;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;

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

    /// <summary>Panel border while an acceptable external drag hovers over the shelf (Task 13).</summary>
    private static readonly Brush DropAcceptBorderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)));

    /// <summary>Panel border the rest of the time. A field, not the Brushes.Transparent constant
    /// inline, only so every reset goes through one named value.</summary>
    private static readonly Brush DropIdleBorderBrush = System.Windows.Media.Brushes.Transparent;

    /// <summary>How long the inline status line (a failed copy/drag) stays up.</summary>
    private static readonly TimeSpan StatusDuration = TimeSpan.FromSeconds(2.5);

    /// <summary>Tag on the Link card's "open in the browser" hotspot. Must match Cards.xaml.</summary>
    private const string LinkOpenTag = "LinkOpen";

    private readonly DispatcherTimer _retractTimer;
    private readonly DispatcherTimer _statusTimer;

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

    // Card interaction: press records where and on what; a move past the system drag threshold
    // promotes it to a drag; a release before that is a click (= re-copy).
    private Point _pressPoint;
    private CardViewModel? _pressedCard;
    private UIElement? _captureHost;
    private bool _isDragging;

    // Drag-IN (Task 13): true for the whole time an OLE drag -- from Explorer, a browser, or the
    // shelf's own outbound drag looping back -- is hovering over the shelf. See IsPointerInside.
    private bool _isDragOver;

    public ShelfWindow()
    {
        InitializeComponent();

        _retractTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _retractTimer.Tick += OnRetractTick;

        _statusTimer = new DispatcherTimer { Interval = StatusDuration };
        _statusTimer.Tick += OnStatusTick;

        MouseEnter += OnPointerEnter;
        MouseLeave += OnPointerLeave;
        IsVisibleChanged += OnSelfVisibleChanged;
        DpiChanged += OnDpiChanged;

        // Drag-IN (Task 13): AllowDrop="True" is set in XAML; these four cover the whole hover
        // lifecycle of an external drag over the shelf. Handlers on the Window itself, not on
        // any one child -- DragEnter/Over/Leave/Drop are bubbling routed events, so whichever
        // descendant is actually under the pointer (a card, the search box, empty Grid space)
        // still reaches here.
        DragEnter += OnShelfDragEnter;
        DragOver += OnShelfDragOver;
        DragLeave += OnShelfDragLeave;
        Drop += OnShelfDrop;

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
    /// True while the pointer is over the shelf, while keyboard focus is inside it (typing in
    /// the search box), while the user is dragging a card out of it, or while an external drag
    /// is hovering over it waiting to be dropped. Drives the retract timer.
    /// <para>Without the keyboard-focus term, a user who clicks into the search box and then types
    /// without the mouse moving would get retracted out from under them mid-sentence once the
    /// existing hover countdown elapses. Without the drag term, dragging a card away from the
    /// shelf raises MouseLeave the instant the pointer crosses the edge -- and because
    /// DoDragDrop pumps its own message loop, the retract countdown would fire and slide the drag
    /// source out from under the drag while it is still in progress.</para>
    /// <para>Without the drag-OVER term (<see cref="_isDragOver"/>), a drag-IN from another app
    /// (Task 13) would retract the shelf out from under the drop: while any OLE drag session is
    /// in progress, Windows routes DragEnter/DragOver/DragLeave to the target through
    /// <c>IDropTarget</c> instead of ordinary WM_MOUSEMOVE, so this window never gets a
    /// MouseEnter for the pointer arriving with a payload in tow -- <see cref="_pointerInside"/>
    /// and <see cref="IsMouseOver"/> both stay false the entire time a file is hovering over the
    /// shelf waiting to be dropped, and a retract timer already ticking down from an earlier,
    /// unrelated MouseLeave would fire mid-hover with nothing here to stop it.</para>
    /// </summary>
    public bool IsPointerInside => _pointerInside || _isDragging || _isDragOver || IsMouseOver || IsKeyboardFocusWithin;

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

        // Same failure mode for the drag-over flag: Hide() can happen mid-drag-over (ApplySettings
        // runs on a DPI/monitor change, HoverEnabledChanged toggles off, etc.) without a DragLeave
        // ever reaching the window -- OLE only delivers DragLeave for a drag that actually left
        // the drop target's screen area, not for the target disappearing out from under it. Left
        // set, _isDragOver would keep IsPointerInside permanently true exactly like _pointerInside
        // above, and the accept-border would still be showing the next time the shelf slides in
        // even though nothing is being dragged anymore.
        _isDragOver = false;
        Panel.BorderBrush = DropIdleBorderBrush;

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

        // Drag-out / click-to-copy, wired once per list instead of per card: the card visual lives
        // in a shared DataTemplate (Cards.xaml) with no code-behind, and its containers are
        // recycled by virtualization, so per-element handlers would have to be re-attached
        // constantly.
        //
        // handledEventsToo:true is belt-and-braces, not a requirement: measured (all live
        // interaction checks still pass with it false) because the two button events are Preview
        // ones, which run before ListBoxItem's bubbling selection handling ever gets to mark
        // anything handled. It stays on so a control added to the card template later cannot
        // silently swallow the gesture.
        foreach (var host in new ItemsControl[] { CardsList, PinnedList })
        {
            host.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler(OnCardPreviewMouseLeftButtonDown), true);
            host.AddHandler(UIElement.MouseMoveEvent,
                new MouseEventHandler(OnCardMouseMove), true);
            host.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent,
                new MouseButtonEventHandler(OnCardPreviewMouseLeftButtonUp), true);
            host.LostMouseCapture += OnCardHostLostMouseCapture;
        }

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

    /// <summary>
    /// Press on a card: remember what and where, so the following move/release can be classified
    /// as a drag or a click. Nothing is committed here -- a press that turns out to be a scroll
    /// gesture or a press on the pin/delete buttons must leave the card alone.
    /// </summary>
    private void OnCardPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressedCard = null;

        // The action bar's own buttons own their clicks (see OnCardActionClick). Swallowing them
        // here would make Delete re-copy the card instead of deleting it.
        if (IsWithinActionButton(e.OriginalSource))
        {
            return;
        }

        var card = CardFrom(e.OriginalSource);
        if (card is null)
        {
            return;
        }

        _pressPoint = e.GetPosition(this);

        // Capture BEFORE arming _pressedCard, not after. Taking the mouse makes WPF re-evaluate
        // where the pointer is and can synthesize a MouseMove there and then -- which lands in
        // OnCardMouseMove. Armed first, that move would find the press already pending and, if the
        // button reads as up at that instant (a fast click released between the button-down
        // message and this handler running), cancel the very press that asked for the capture.
        // Arming last makes the synthesized move a no-op. Measured: with the old order the live
        // probe's press-and-release checks cleared the press at birth, intermittently.
        CaptureForGesture(sender as UIElement);
        _pressedCard = card;
    }

    /// <summary>
    /// Takes the mouse for the press-to-drag-or-click gesture, so the moves and the release that
    /// decide which one it is still arrive after the pointer has left the list.
    /// <para>Without this, a press followed by a fast flick off the list -- easy to do, since the
    /// shelf is flush against a screen edge and the pointer leaves the window within a few pixels
    /// of it -- produces neither a drag nor a click: no further MouseMove reaches the list, so the
    /// 4px threshold is never evaluated, and the MouseUp lands on some other element entirely.</para>
    /// <para><see cref="CaptureMode.SubTree"/>, not Element: element capture routes every mouse
    /// event to the list itself and rewrites OriginalSource, which would blind the pin/delete and
    /// link-hotspot checks (they read OriginalSource). SubTree keeps normal hit-testing for
    /// anything inside the list and only redirects what happens outside it.</para>
    /// <para>Presses that land on the action-bar buttons never get here: those return before
    /// <see cref="_pressedCard"/> is set, so ButtonBase's own capture is left alone.</para>
    /// </summary>
    private void CaptureForGesture(UIElement? host)
    {
        if (host is null)
        {
            return;
        }

        // Best-effort. Capture can be refused (another app already owns it); the gesture then
        // behaves exactly as it did before capture was introduced rather than breaking.
        if (Mouse.Capture(host, CaptureMode.SubTree))
        {
            _captureHost = host;
        }
    }

    private void ReleaseGestureCapture()
    {
        var host = _captureHost;
        _captureHost = null;

        if (host is not null && ReferenceEquals(Mouse.Captured, host))
        {
            host.ReleaseMouseCapture();
        }
    }

    /// <summary>
    /// Capture went away -- either we released it, or something outside took it (another window,
    /// an Alt+Tab, a menu). Abandon the pending gesture: acting on a press whose release we will
    /// never see would re-copy or drag a card the user has already moved on from.
    /// </summary>
    private void OnCardHostLostMouseCapture(object sender, MouseEventArgs e)
    {
        // LostMouseCapture bubbles, so a Button inside a card releasing its own capture reaches
        // this handler too. Only the host's own loss is ours to act on.
        if (!ReferenceEquals(sender, e.OriginalSource))
        {
            return;
        }

        _captureHost = null;
        _pressedCard = null;
    }

    /// <summary>
    /// Promotes a press into a native drag once the pointer has moved past the system drag
    /// threshold. Below the threshold this does nothing, which is what lets a click stay a click.
    /// </summary>
    private void OnCardMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedCard is null || _isDragging)
        {
            return;
        }

        // The button came up somewhere we never saw (drag onto another window, a lost capture):
        // the press is stale and must not turn into a drag on the next stray move.
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _pressedCard = null;
            ReleaseGestureCapture();
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var card = _pressedCard;
        _pressedCard = null;

        // Hand the mouse over before DoDragDrop: the drag loop takes the capture itself, and
        // holding ours into it would fight it for the pointer.
        ReleaseGestureCapture();

        BeginCardDrag(sender as FrameworkElement ?? this, card);
    }

    /// <summary>
    /// Release without having crossed the drag threshold: a click. Re-copies the card to the
    /// clipboard, except on a Link card's domain hotspot, which opens the browser instead.
    /// </summary>
    private void OnCardPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var card = _pressedCard;
        _pressedCard = null;

        // Released before anything else runs, and after _pressedCard is already cleared, so the
        // LostMouseCapture this raises is a no-op rather than a second path into the same state.
        ReleaseGestureCapture();

        if (card is null || _isDragging || IsWithinActionButton(e.OriginalSource))
        {
            return;
        }

        if (card.Kind == ClipKind.Link && IsWithinLinkOpenArea(e.OriginalSource))
        {
            OpenLink(card);
        }
        else
        {
            CopyCardToClipboard(card);
        }

        e.Handled = true;
    }

    /// <summary>
    /// Runs the outbound drag with the retract countdown suspended for its whole duration.
    /// <para><see cref="DragDropSource.TryStartDrag"/> blocks (it pumps its own message loop), so
    /// everything after it runs only once the drop has completed or been cancelled.</para>
    /// </summary>
    private void BeginCardDrag(FrameworkElement source, CardViewModel card)
    {
        _isDragging = true;
        _retractTimer.Stop();

        try
        {
            if (!DragDropSource.TryStartDrag(source, card.Item, BlobsDir))
            {
                ReportContentMissing(card, "drag");
            }
        }
        finally
        {
            _isDragging = false;

            // The pointer is wherever the drop left it -- quite possibly off the shelf, with the
            // MouseLeave already consumed mid-drag. Re-evaluate rather than assume.
            _pointerInside = IsMouseOver;
            ArmRetractIfPointerOutside();
        }
    }

    /// <summary>
    /// A drag carrying an acceptable payload has entered the shelf. Suspends the retract
    /// countdown for the duration (see the drag-over term on <see cref="IsPointerInside"/>) and,
    /// for anything other than the shelf's own card looping back on itself, brightens the panel
    /// border as the accept affordance.
    /// </summary>
    private void OnShelfDragEnter(object sender, DragEventArgs e)
    {
        _isDragOver = true;
        _retractTimer.Stop();

        ApplyDragVisual(e);
        e.Handled = true;
    }

    /// <summary>
    /// Fires continuously while the drag stays over the shelf. WPF requires <see cref="DragEventArgs.Effects"/>
    /// to be (re-)set on every call, not just on DragEnter, or the OS cursor reverts to
    /// no-drop -- so this re-evaluates the same acceptability check DragEnter already showed the
    /// border for, rather than assuming it still holds.
    /// </summary>
    private void OnShelfDragOver(object sender, DragEventArgs e)
    {
        ApplyDragVisual(e);
        e.Handled = true;
    }

    /// <summary>
    /// The drag left the shelf without dropping (or moved onto a child that swallowed the event
    /// -- it does not, since every subtree here leaves DragLeave to bubble). Restores the idle
    /// border and lets the retract countdown resume if the real pointer is not over the shelf.
    /// </summary>
    private void OnShelfDragLeave(object sender, DragEventArgs e)
    {
        _isDragOver = false;
        Panel.BorderBrush = DropIdleBorderBrush;
        ArmRetractIfPointerOutside();
        e.Handled = true;
    }

    /// <summary>
    /// The actual drop. Self-drag (see <see cref="DragDropTarget.IsSelfDrag"/>) is ignored
    /// entirely -- no card, no visual, matching the "no add, no visual" requirement -- since the
    /// payload's own <see cref="DragDropSource.CardIdFormat"/> marker means this is a card the
    /// shelf already holds looping back on itself. Anything else that classifies to a
    /// <see cref="CapturedClip"/> is routed through <see cref="App.NotifyManualCapture"/> --
    /// the SAME pipeline entry point a real clipboard capture uses -- so dedup, stacking, the
    /// indicator flash and the capture sound all behave identically to a real capture instead of
    /// a second, drifting copy of that logic living here.
    /// </summary>
    private void OnShelfDrop(object sender, DragEventArgs e)
    {
        _isDragOver = false;
        Panel.BorderBrush = DropIdleBorderBrush;

        var clip = DragDropTarget.ClipFromDataObject(e.Data);
        e.Effects = clip is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        if (clip is not null)
        {
            global::TNDrop.App.NotifyManualCapture(clip);
        }

        ArmRetractIfPointerOutside();
    }

    /// <summary>
    /// Shared DragEnter/DragOver logic: sets both the OS drop cursor (<see cref="DragEventArgs.Effects"/>)
    /// and the panel border affordance from the same acceptability check, so the two can never
    /// show contradictory answers (a Copy cursor over an un-highlighted border, or vice versa).
    /// A self-drag is deliberately unacceptable here too -- "no visual" for that case means the
    /// border must stay idle, which falls straight out of <see cref="DragDropTarget.HasAcceptablePayload"/>
    /// already excluding it.
    /// </summary>
    private void ApplyDragVisual(DragEventArgs e)
    {
        var acceptable = DragDropTarget.HasAcceptablePayload(e.Data);
        Panel.BorderBrush = acceptable ? DropAcceptBorderBrush : DropIdleBorderBrush;
        e.Effects = acceptable ? DragDropEffects.Copy : DragDropEffects.None;
    }

    /// <summary>
    /// Click-to-copy: puts the card back on the clipboard without TNDrop re-capturing its own
    /// write, optionally floats the item to the top of the shelf, and confirms with the same
    /// indicator flash + sound a real capture produces.
    /// </summary>
    private void CopyCardToClipboard(CardViewModel card)
    {
        var item = card.Item;

        // Before the write, not after: the clipboard notification can arrive before SetX returns.
        global::TNDrop.App.Monitor?.SuppressNext();

        var copied = false;
        switch (item.Kind)
        {
            case ClipKind.Text:
            case ClipKind.Link:
                if (!string.IsNullOrEmpty(item.Text))
                {
                    ClipboardIo.SetText(item.Text);
                    copied = true;
                }

                break;

            case ClipKind.Files:
                // Same resolution the drag payload uses, so a click and a drag of the same card
                // can never disagree about which of its paths still exist.
                var paths = DragDropSource.ExistingPaths(item);
                if (paths.Length > 0)
                {
                    ClipboardIo.SetFiles(paths);
                    copied = true;
                }

                break;

            case ClipKind.Image:
                var image = DragDropSource.LoadImage(item, BlobsDir);
                if (image is not null)
                {
                    ClipboardIo.SetImage(image);
                    copied = true;
                }

                break;
        }

        if (!copied)
        {
            ReportContentMissing(card, "copy");
            return;
        }

        if (global::TNDrop.App.Settings?.MoveToTopOnCopy == true)
        {
            // Rebuilds the card list underneath us (ItemStore.Changed). Safe here: this is the
            // tail of the click, and _pressedCard was already cleared.
            _itemStore?.MoveToTop(item.Id);
        }

        var settings = global::TNDrop.App.Settings;
        if (settings is not null)
        {
            global::TNDrop.App.Indicator?.Flash(settings.IndicatorStyle, settings.Edge);
        }

        global::TNDrop.App.Sounds?.PlayCapture();
    }

    /// <summary>Opens a Link card's URL in the user's default browser.</summary>
    private void OpenLink(CardViewModel card)
    {
        var url = card.Item.Text;

        // Defence in depth. The card is only Kind==Link because UrlDetector said so at capture
        // time, but re-checking here is what keeps ShellExecute from ever being handed something
        // that is not an http/https URL -- ShellExecute happily runs file paths and other schemes,
        // and the string came off the clipboard.
        if (string.IsNullOrWhiteSpace(url) || !UrlDetector.IsUrl(url))
        {
            FileLogger.Instance?.Warn(Module,
                $"refused to open a {card.Kind} card's content as a URL: not an http/https address");
            return;
        }

        try
        {
            // UseShellExecute is what hands the URL to the registered browser rather than trying
            // to exec it as a program.
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // A malformed/unregistered scheme must not take the shelf down. The URL itself is not
            // logged: it is user clipboard content.
            FileLogger.Instance?.Warn(Module, $"failed to open a link in the browser: {ex.Message}");
        }
    }

    /// <summary>
    /// The card's content is gone from disk (every file path deleted, or an image blob that can no
    /// longer be decoded). Tells the user inline and leaves a WARN in the log.
    /// </summary>
    private void ReportContentMissing(CardViewModel card, string action)
    {
        FileLogger.Instance?.Warn(Module,
            $"{action} of a {card.Kind} card found no content left on disk (id {card.Id})");
        ShowStatus(Strings.FileMissing);
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;

        // Restart, don't stack: a second failure resets the full duration rather than inheriting
        // the remainder of the first one's.
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void OnStatusTick(object? sender, EventArgs e)
    {
        _statusTimer.Stop();
        StatusText.Visibility = Visibility.Collapsed;
        StatusText.Text = string.Empty;
    }

    /// <summary>Blobs directory of the live store; empty when there is no store (designer/tests).</summary>
    private string BlobsDir => _itemStore?.BlobsDir ?? string.Empty;

    private static bool IsWithinActionButton(object? source) =>
        FindAncestor<ButtonBase>(source) is not null;

    private static bool IsWithinLinkOpenArea(object? source)
    {
        for (var current = source as DependencyObject; current is not null; current = ParentOf(current))
        {
            if (current is FrameworkElement { Tag: LinkOpenTag })
            {
                return true;
            }

            // Never walk out of the card into the list chrome. Tag is used for other purposes
            // nearby (the action bar's buttons carry Tag="Pin"/"Delete"), so an unbounded walk
            // would be one stray Tag="LinkOpen" away from turning an unrelated click into a
            // browser launch.
            if (current is ItemsControl)
            {
                break;
            }
        }

        return false;
    }

    /// <summary>The CardViewModel the given hit-test source belongs to, or null if it is not on a card.</summary>
    private static CardViewModel? CardFrom(object? source)
    {
        for (var current = source as DependencyObject; current is not null; current = ParentOf(current))
        {
            if (current is FrameworkElement { DataContext: CardViewModel card })
            {
                return card;
            }
        }

        return null;
    }

    private static T? FindAncestor<T>(object? source) where T : DependencyObject
    {
        for (var current = source as DependencyObject; current is not null; current = ParentOf(current))
        {
            if (current is T typed)
            {
                return typed;
            }
        }

        return null;
    }

    /// <summary>
    /// Visual parent, falling back to the logical one. The fallback matters: a hit-test source can
    /// be a non-Visual (a TextBlock's inline Run, for instance), and VisualTreeHelper.GetParent
    /// throws rather than returning null for those.
    /// </summary>
    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);

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
