using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
using Border = System.Windows.Controls.Border;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Color = System.Windows.Media.Color;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using DragEventHandler = System.Windows.DragEventHandler;
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

    /// <summary>
    /// How long a shelf opened BY an in-flight OLE drag (v1.2 Task B, <see cref="SlideInForDrag"/>)
    /// holds itself open before the ordinary retract rules take back over.
    /// <para>Needed because the two events that normally keep the shelf out -- a real MouseEnter
    /// and a DragEnter on the shelf itself -- are both still in the future at the moment the
    /// trigger band opens it: the pointer is carrying a payload (so no WM_MOUSEMOVE ever reaches
    /// this window; see the drag-over paragraph on <see cref="IsPointerInside"/>) and it has not
    /// travelled from the band onto the shelf yet. With a default 800 ms RetractDelayMs the
    /// countdown armed by <see cref="OnSlideInCompleted"/> would otherwise be able to slide the
    /// shelf away while the user is still on their way to it.</para>
    /// <para>3 s, and a deadline rather than a latch, deliberately: it must outlast the 250 ms
    /// slide plus a slow hand, and it must EXPIRE on its own -- a drag abandoned somewhere else on
    /// screen never sends this window a DragEnter or a DragLeave, so a term that only something
    /// else could clear would pin the shelf open with no way for the user to dismiss it (the
    /// trigger band is hidden while the shelf is out).</para>
    /// </summary>
    private static readonly TimeSpan DragOpenGrace = TimeSpan.FromSeconds(3);

    /// <summary>Tag on the Link card's "open in the browser" hotspot. Must match Cards.xaml.</summary>
    private const string LinkOpenTag = "LinkOpen";

    /// <summary>x:Name of a card template's outermost Border. Must match Cards.xaml.</summary>
    private const string CardRootName = "CardRoot";

    /// <summary>Border of a card that would accept the card currently being dragged over it (Task 14).</summary>
    private static readonly Brush MergeAcceptBorderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)));

    /// <summary>Border of a card the rest of the time. Matches Cards.xaml's CardRoot BorderBrush.</summary>
    private static readonly Brush CardIdleBorderBrush = System.Windows.Media.Brushes.Transparent;

    /// <summary>Total length of the refusal shake played on a card that cannot take a merge.</summary>
    private static readonly Duration ShakeDuration = new(TimeSpan.FromMilliseconds(320));

    /// <summary>Chevron spin when the pinned accordion (v1.2 Task H) opens or closes.</summary>
    private static readonly Duration ChevronRotateDuration = new(TimeSpan.FromMilliseconds(160));

    /// <summary>
    /// Share of the shelf's height the pinned accordion may occupy before it starts scrolling
    /// inside itself, and the floor that share is never allowed to fall below.
    /// <para>The accordion's Grid row is Auto and the main card list's is star, so an unbounded
    /// pinned section would take everything it asked for and leave the main list with nothing --
    /// pin a dozen items and the history below them disappears. Applied in <see cref="Place"/>,
    /// from the same resolved rect the window geometry comes from, so it tracks the monitor rather
    /// than being a fixed pixel count that is generous on a 4K panel and crushing on a laptop.</para>
    /// </summary>
    private const double PinnedMaxHeightFraction = 0.40;
    private const double PinnedMinMaxHeightDip = 140;

    private readonly DispatcherTimer _retractTimer;
    private readonly DispatcherTimer _statusTimer;

    // v1.5 追補: 自動格納の抑止 (ヘッダーのピンボタン)。Settings.ShelfPinned と常に同値。
    // 書き込みはピンボタンのクリックハンドラだけ (Task B)、ここは ApplySettings で読むのみ。
    private bool _pinned;

    private AppSettings? _settings;
    private EdgeSide _edge = EdgeSide.Left;
    private double _shownX;
    private double _hiddenX = -ShelfPlacement.ShelfWidth;
    private MonitorGeometry.WorkArea _area;
    private ShelfPlacement.Rect _rect;
    private bool _placed;

    // v1.7.1: トリガー帯の矩形キャッシュ。Place() でのみ更新 (EdgeTriggerWindow の
    // _hintTriggerRect と同じ流儀 -- tick の hot path で TriggerRect を再計算しない)。
    // 算出は EdgeTriggerWindow.Place と同じ唯一の関数 ShelfPlacement.TriggerRect。
    private ShelfPlacement.Rect _holdTriggerRect;
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

    // Drag-hover open (v1.2 Task B): UTC instant until which a shelf opened by a drag over the
    // trigger band holds itself open. DateTime.MinValue = not drag-opened (or the drag has since
    // arrived / the shelf has since hidden). See DragOpenGrace.
    private DateTime _dragOpenGraceUntil = DateTime.MinValue;

    // Stack UX (Task 14): the one expanded-stack popup, created on first use, and the card border
    // currently lit up as a merge target (null when no acceptable card drag is hovering).
    private StackFlyout? _stackFlyout;
    private Border? _mergeHighlight;

    // Id of the card the flyout was showing when the press landed, read when the release decides
    // whether the click is an open or a close. It cannot be re-derived at release time: see
    // OnCardPreviewMouseLeftButtonDown. An Id rather than a bool so the latch can only ever answer
    // for the card it was taken on, without depending on _pressedCard still being that card.
    private string? _flyoutShownAtPressId;

    // Pinned accordion (v1.2 Task H): whether the section is open. Mirrors
    // AppSettings.PinnedExpanded -- seeded from it in ApplySettings, written back through
    // App.SetPinnedExpanded by the header toggle. Defaults to true so a ShelfWindow built without
    // ever receiving settings (the designer, a probe) shows the pinned cards rather than hiding
    // them behind a control the user has not touched.
    private bool _pinnedExpanded = true;

    public ShelfWindow()
    {
        InitializeComponent();

        // Header app name, from resources -- same pattern as TrayIcon.cs's own Text/tooltip --
        // rather than a literal baked into ShelfWindow.xaml, so the two never drift apart and an
        // en-locale build shows the en resx's AppName here too.
        PlaceholderTitle.Text = Strings.AppName;

        // Pinned accordion (v1.2 Task H). Wired here rather than in InitializeCardList because
        // that method returns early when there is no store (designer/probe hosts), and the header
        // must still render its label and answer a click in those hosts. The initial count comes
        // from UpdatePinnedVisibility below; the open/closed state arrives later, via ApplySettings.
        PinnedHeaderButton.ToolTip = Strings.PinnedToggleTooltip;
        PinnedHeaderButton.Click += (_, _) => TogglePinnedExpanded();
        UpdatePinnedVisibility();

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
        //
        // KEEP THESE AS `+=`. Task 14's card-level merge handlers preempt them by marking the
        // event handled, and that only works because these are registered without
        // handledEventsToo. Switching any of them to AddHandler(..., handledEventsToo: true)
        // would silently resurrect the shelf-wide accept border underneath a merge and let
        // OnShelfDrop run a second time on a drop a card has already consumed.
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
    /// <para>Without the stack-flyout term (Task 14), the expanded stack popup -- its own top-level
    /// window, so the pointer being on it means the pointer is NOT on the shelf -- would be
    /// retracted out from under the user while they were reading it. The flyout closes itself once
    /// the pointer has been off both it and the shelf for a moment (StackFlyout.OnHoverTick), which
    /// is what stops this term from pinning the shelf open for good.</para>
    /// <para>Without the drag-open grace term (v1.2 Task B), a shelf opened by a drag hovering the
    /// trigger band would have nothing holding it out during the travel from the band to the shelf
    /// -- see <see cref="DragOpenGrace"/>, which also explains why that term expires by itself.</para>
    /// </summary>
    public bool IsPointerInside =>
        _pointerInside || _isDragging || _isDragOver || IsWithinDragOpenGrace
        || IsStackFlyoutOpen || IsMouseOver || IsKeyboardFocusWithin;

    /// <summary>True while a drag-opened shelf is still inside its <see cref="DragOpenGrace"/>.</summary>
    private bool IsWithinDragOpenGrace => DateTime.UtcNow < _dragOpenGraceUntil;

    private bool IsStackFlyoutOpen => _stackFlyout is not null && _stackFlyout.IsOpen;

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

        // Pinned accordion (v1.2 Task H). Settings reach this window only through here, so this is
        // where the persisted open/closed state is picked up. Re-read on every ApplySettings (a DPI
        // change, an edge switch) rather than once at startup: the header toggle saves through
        // App.SetPinnedExpanded before this can run again, so re-reading always finds the value the
        // user last chose -- it can never revert a toggle.
        SetPinnedExpanded(s.PinnedExpanded);

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
        _holdTriggerRect = ShelfPlacement.TriggerRect(
            new ShelfPlacement.Rect(area.X, area.Y, area.W, area.H),
            s.Edge, s.TriggerProximityPx, s.HotZonePercent, s.TriggerAlign);
        _shownX = rect.X;
        _hiddenX = ShelfPlacement.HiddenX(rect, _edge);

        Panel.CornerRadius = _edge == EdgeSide.Left
            ? new CornerRadius(0, 12, 12, 0)
            : new CornerRadius(12, 0, 0, 12);

        // Pinned accordion's scroll cap (v1.2 Task H), from the same rect the window is sized
        // from -- see PinnedMaxHeightFraction for why the section needs a ceiling at all.
        PinnedScroll.MaxHeight = Math.Max(PinnedMinMaxHeightDip, rect.H * PinnedMaxHeightFraction);

        _retractTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(s.RetractDelayMs, 100, 10_000));
        _pinned = s.ShelfPinned;
        UpdatePinButtonVisual();

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

    /// <summary>
    /// <see cref="SlideIn"/> for the one caller that is an in-flight OLE drag rather than a
    /// pointer: the trigger band's DragEnter (v1.2 Task B, routed through App's hover gate so the
    /// HoverEnabled/fullscreen rules are identical to a hover-open). Starts the
    /// <see cref="DragOpenGrace"/> so the shelf is still there when the drag arrives on it.
    /// <para>A separate entry point rather than a flag on <see cref="SlideIn"/>: every other caller
    /// (hover, an interrupted retract, ApplySettings) must keep the ordinary countdown, and the
    /// grace is meaningless -- or actively wrong, since it pins the shelf out -- for them.</para>
    /// </summary>
    public void SlideInForDrag()
    {
        // Before SlideIn, not after: SlideIn can complete synchronously enough to arm the countdown
        // through OnSlideInCompleted, and that arming reads IsPointerInside.
        _dragOpenGraceUntil = DateTime.UtcNow + DragOpenGrace;
        SlideIn();
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

        // First: the flyout is a separate top-level window and would be left hanging in mid-air
        // over the desktop once the shelf it belongs to has slid away.
        CloseStackFlyout();

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

        // And the drag-open grace: it is a property of THIS appearance of the shelf. Left standing,
        // it would suppress the retract of the NEXT slide-in for whatever is left of its 3 s.
        _dragOpenGraceUntil = DateTime.MinValue;

        // Same again for the two Task 14 visuals. The flyout would otherwise float over the
        // desktop with no shelf under it (and, being counted by IsPointerInside, would keep the
        // retract suppressed for the next slide-in); a merge highlight left set would come back
        // lit on a recycled container.
        CloseStackFlyout();
        ClearMergeHighlight();

        // Whatever temporarily allowed real activation for the search box (see
        // OnSearchBoxPreviewMouseLeftButtonDown) must not survive the shelf going away, or the
        // *next* slide-in would start pre-activated instead of NOACTIVATE. Idempotent if the
        // search box was never focused this time around.
        WindowStyles.SetNoActivate(this, true);

        // Guarded on IsKeyboardFocusWithin (Task 17 review note, carried over from a Task 10
        // caveat): Keyboard.ClearFocus() clears WPF's single, process-wide FocusedElement, not
        // just this window's. Called unconditionally, it would steal focus from an unrelated
        // window -- e.g. a control in the Task 17 settings window -- if the shelf happened to hide
        // while that window held keyboard focus. The only element this window ever grants real
        // keyboard focus to is its own SearchBox (see OnSearchBoxPreviewMouseLeftButtonDown), so
        // this guard changes nothing for the case the call exists for: IsKeyboardFocusWithin is
        // true exactly when there is search-box focus left to clear.
        if (IsKeyboardFocusWithin)
        {
            Keyboard.ClearFocus();
        }
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

        // The rule itself lives in ShelfRetract.ShouldArm -- pure, and unit-tested against the
        // exact defect the v1.2 Task B probe caught (a drag-opened shelf that armed no timer and so
        // could never notice its own grace expiring). This method is only the wiring: read the live
        // state, ask, start.
        if (ShelfRetract.ShouldArm(IsVisible, _pinned, IsPointerInside, IsWithinDragOpenGrace))
        {
            _retractTimer.Start();
        }
    }

    private void OnRetractTick(object? sender, EventArgs e)
    {
        if (_pinned)
        {
            // ピン操作とタイマー発火の競合対策の二重ガード。ピンした瞬間にクリックハンドラ
            // (Task B) が Stop するが、その前にキューされた 1 tick がここに届き得る。
            _retractTimer.Stop();
            return;
        }

        if (IsPointerInside || IsCursorHoldingShelf())
        {
            // Suppressed, not cancelled. Re-arm rather than drop the timer: if IsPointerInside is
            // wrong (or the MouseLeave that would normally re-arm never arrives), dropping it here
            // is what wedges the shelf open permanently. IsCursorHoldingShelf は静止カーソル対策
            // (v1.7.1): イベントが死んでいても座標で「上にいる」を検知する。
            _retractTimer.Stop();
            _retractTimer.Start();
            return;
        }

        _retractTimer.Stop();
        SlideOut();
    }

    /// <summary>
    /// v1.7.1: the retract tick's physical-cursor term. See ShelfRetract.CursorHolds for the
    /// full rationale (stationary cursor = no mouse events = every event-derived term dead).
    /// Win32/geometry failure returns false so behaviour degrades to the pre-v1.7.1 rules
    /// rather than pinning the shelf open.
    /// </summary>
    private bool IsCursorHoldingShelf()
    {
        if (!_placed)
        {
            return false;
        }

        try
        {
            var (cursorX, cursorY) = MonitorGeometry.CursorDip(_area);
            return ShelfRetract.CursorHolds(cursorX, cursorY, _rect, _holdTriggerRect);
        }
        catch
        {
            return false;
        }
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

        // Header (v1.1 Task C): ⚙ opens the same settings window the tray's own "設定..." menu
        // item opens (App.OpenSettingsWindow -- see that method's doc comment for why it exists
        // alongside the tray's private event handler), × slides the shelf out exactly like the
        // retract timer. Tooltips only, no Content text -- Header.IconButtonStyle sizes these as
        // small square icon buttons.
        // Pin (v1.5 追補): クリックでトグルし、見た目・タイマー・永続化をこの場で更新する。
        // Settings への書き込み経路はこのハンドラ経由の App.SetShelfPinned だけ。
        HeaderPinButton.Click += (_, _) => OnPinButtonClick();
        UpdatePinButtonVisual();

        HeaderSettingsButton.ToolTip = Strings.HeaderSettingsTooltip;
        HeaderSettingsButton.Click += (_, _) => global::TNDrop.App.OpenSettingsWindow();

        HeaderHelpButton.ToolTip = Strings.HelpButtonTooltip;
        HeaderHelpButton.Click += (_, _) => OnHelpButtonClick();

        HeaderHideButton.ToolTip = Strings.HeaderHideTooltip;
        HeaderHideButton.Click += (_, _) => SlideOut();

        SearchPlaceholderText.Text = Strings.SearchPlaceholder;
        SearchBox.TextChanged += OnSearchTextChanged;
        OnSearchTextChanged(SearchBox, null!);

        // The shelf never activates on its own (WS_EX_NOACTIVATE, see OnSourceInitialized) so
        // typing normally goes to whichever app was focused before the shelf slid in. Clicking
        // the search box is the one deliberate exception the design calls for: grant real
        // activation just long enough to type a query, then revoke it once focus leaves.
        SearchBox.PreviewMouseLeftButtonDown += OnSearchBoxPreviewMouseLeftButtonDown;
        SearchBox.LostKeyboardFocus += OnSearchBoxLostKeyboardFocus;

        // Search clear button (v1.2 Task F). Tooltip set here, same pattern as every other
        // header/footer control in this method.
        SearchClearButton.ToolTip = Strings.SearchClearTooltip;
        SearchClearButton.Click += OnSearchClearButtonClick;

        ClearButton.Content = Strings.ClearButton;
        ClearButton.Click += OnClearButtonClick;

        SelectAllButton.Content = Strings.SelectAll;
        CopySelectedButton.Content = Strings.CopySelected;
        DeleteSelectedButton.Content = Strings.DeleteSelected;
        ClearSelectionButton.Content = Strings.ClearSelection;
        SelectAllButton.Click += (_, _) => _shelfViewModel?.SelectAllVisible();
        CopySelectedButton.Click += OnCopySelectedClick;
        DeleteSelectedButton.Click += OnDeleteSelectedClick;
        ClearSelectionButton.Click += (_, _) => _shelfViewModel?.ClearSelection();

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

            // Card-to-card merge (Task 14). These sit BELOW the window-level drag-IN handlers in
            // the tree, so they run first and, for a merge payload, mark the event handled -- the
            // window's own handlers (registered without handledEventsToo) then do not run, and the
            // shelf-wide accept border never competes with the per-card one. Anything that is not
            // a merge payload is left alone here and bubbles on to the window exactly as before.
            host.AddHandler(UIElement.DragEnterEvent, new DragEventHandler(OnCardDragOver));
            host.AddHandler(UIElement.DragOverEvent, new DragEventHandler(OnCardDragOver));
            host.AddHandler(UIElement.DragLeaveEvent, new DragEventHandler(OnCardDragLeave));
            host.AddHandler(UIElement.DropEvent, new DragEventHandler(OnCardDrop));
        }

        _shelfViewModel.PinnedCards.CollectionChanged += (_, _) => UpdatePinnedVisibility();
        _shelfViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null || e.PropertyName.StartsWith("Count", StringComparison.Ordinal))
            {
                UpdateFilterTabs();
            }

            if (e.PropertyName is null or nameof(ShelfViewModel.SelectionMode) or nameof(ShelfViewModel.SelectedCount))
            {
                UpdateSelectionBar();
            }

            // TotalCount/VisibleCount are both re-raised by every Rebuild (Filter/SearchText
            // changes and store-driven ones alike -- see ShelfViewModel.Rebuild), so watching just
            // these two covers every path the footer count needs to track without also needing
            // Filter/SearchText/IsFilterActive in this list.
            if (e.PropertyName is null or nameof(ShelfViewModel.TotalCount) or nameof(ShelfViewModel.VisibleCount))
            {
                UpdateFooterCount();
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
        UpdateSelectionBar();
        UpdateFooterCount();
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

    /// <summary>
    /// The one place the pinned accordion's three visuals are resolved together (v1.2 Task H): the
    /// section's own visibility, the card list's visibility, and the header's count text. Called
    /// from every path that can change either the pinned COUNT (PinnedCards.CollectionChanged) or
    /// the OPEN state (<see cref="SetPinnedExpanded"/>), so the header can never claim a count the
    /// list below it does not show.
    /// <para>Zero pinned items hides the whole section, header included, rather than showing a
    /// header that says 0: the accordion exists to get pinned cards out of the way, and there is
    /// nothing to get out of the way. The expanded/collapsed state survives that -- it lives in
    /// <see cref="_pinnedExpanded"/> and the settings file, not in the visibility -- so pinning
    /// something again brings the section back in whichever state the user left it.</para>
    /// </summary>
    private void UpdatePinnedVisibility()
    {
        var count = _shelfViewModel?.PinnedCards.Count ?? 0;

        PinnedSection.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PinnedScroll.Visibility = _pinnedExpanded ? Visibility.Visible : Visibility.Collapsed;
        PinnedHeaderText.Text = string.Format(CultureInfo.CurrentUICulture, Strings.PinnedHeaderFormat, count);
    }

    /// <summary>
    /// Sets the accordion's open state and re-renders it. Does NOT persist -- callers that
    /// represent a user action (<see cref="TogglePinnedExpanded"/>) save, the caller that is
    /// merely reading the saved value back (<see cref="ApplySettings"/>) must not, or a re-place
    /// during startup would write the value it just read.
    /// </summary>
    private void SetPinnedExpanded(bool expanded)
    {
        // No-op when nothing changes. Matters because ApplySettings calls this on every re-place
        // (a DPI change, an edge switch, a monitor unplug), and without this the chevron would
        // replay its 160 ms spin each time for a state that did not move. The header COUNT does not
        // depend on this early return: PinnedCards.CollectionChanged drives its own
        // UpdatePinnedVisibility, and the constructor plus InitializeCardList each run one.
        // Safe because the visuals are never established BY this method: the constructor calls
        // UpdatePinnedVisibility directly, and that method is the only writer of the two
        // visibilities and derives both from _pinnedExpanded, so the field and the screen cannot be
        // out of step at the moment this returns early.
        if (_pinnedExpanded == expanded)
        {
            return;
        }

        _pinnedExpanded = expanded;
        UpdatePinnedVisibility();

        // Expanded = chevron up (0 deg), collapsed = chevron down (180 deg). Animated rather than
        // assigned so the rotation reads as the section folding away; FillBehavior.HoldEnd because
        // unlike the shake this IS the resting state, not a transient effect.
        var animation = new DoubleAnimation(expanded ? 0 : 180, ChevronRotateDuration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        PinnedChevronRotation.BeginAnimation(RotateTransform.AngleProperty, animation);
    }

    /// <summary>The header click: flip the accordion and remember the new state.</summary>
    private void TogglePinnedExpanded()
    {
        SetPinnedExpanded(!_pinnedExpanded);
        global::TNDrop.App.SetPinnedExpanded(_pinnedExpanded);
    }

    /// <summary>Shows/hides the batch action bar and refreshes its count label from
    /// <see cref="ShelfViewModel.SelectionMode"/>/<see cref="ShelfViewModel.SelectedCount"/>.</summary>
    private void UpdateSelectionBar()
    {
        if (_shelfViewModel is null)
        {
            return;
        }

        SelectionBar.Visibility = _shelfViewModel.SelectionMode ? Visibility.Visible : Visibility.Collapsed;
        SelectedCountText.Text = string.Format(
            CultureInfo.CurrentUICulture, Strings.SelectedCountFormat, _shelfViewModel.SelectedCount);
    }

    /// <summary>
    /// Refreshes the footer's file-count line from <see cref="ShelfViewModel.TotalCount"/> /
    /// <see cref="ShelfViewModel.VisibleCount"/> / <see cref="ShelfViewModel.IsFilterActive"/> -
    /// the VM's single resolution for what those numbers are (see its own doc comments, and note
    /// v1.4 Task A: both are now file counts weighted the same way the filter badges are, not
    /// card counts), so this method only formats them, never recomputes them.
    /// <para>Only updates <see cref="CountText"/>'s text, not its Visibility: while
    /// <see cref="StatusText"/> is showing a transient failure message, the two share the same
    /// footer cell and <see cref="ShowStatus"/>/<see cref="OnStatusTick"/> own that switch. Text
    /// is still kept current here so it is correct the moment the status message clears.</para>
    /// </summary>
    private void UpdateFooterCount()
    {
        if (_shelfViewModel is null)
        {
            return;
        }

        CountText.Text = _shelfViewModel.IsFilterActive
            ? string.Format(CultureInfo.CurrentUICulture, Strings.FilteredCountFormat,
                _shelfViewModel.VisibleCount, _shelfViewModel.TotalCount)
            : string.Format(CultureInfo.CurrentUICulture, Strings.TotalCountFormat, _shelfViewModel.TotalCount);
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        var hasText = !string.IsNullOrEmpty(SearchBox.Text);

        SearchPlaceholderText.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;

        // Search clear button (v1.2 Task F): visible only while there is something to clear.
        SearchClearButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The search box's × clear button (v1.2 Task F). Clears <see cref="ShelfViewModel.SearchText"/>
    /// through the same TwoWay binding the user's own typing goes through (SearchBox.Text ->
    /// SearchText, UpdateSourceTrigger=PropertyChanged in ShelfWindow.xaml) rather than reaching
    /// into the view model directly, so there is exactly one path that ever changes SearchText.
    /// <para>Restores keyboard focus to the search box only if it already had it: the button
    /// itself is Focusable="False" (Header.IconButtonStyle) so a click never grabs focus on its
    /// own, but the box's OWN focus could otherwise be knocked loose by the click landing outside
    /// it. When the box did not have focus to begin with (e.g. SearchText was set some other way),
    /// this does nothing extra - the button must not steal activation it was not asked for.</para>
    /// </summary>
    private void OnSearchClearButtonClick(object sender, RoutedEventArgs e)
    {
        var hadFocus = SearchBox.IsKeyboardFocusWithin;

        SearchBox.Text = string.Empty;

        if (hadFocus)
        {
            Keyboard.Focus(SearchBox);
        }
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
            case "Edit":
                // Opens (or fronts) the editor; nothing in the store changed yet, so skip the
                // unconditional Save() below - it exists for Pin/Delete which mutate immediately.
                global::TNDrop.App.OpenEditDialog(card.Id, card.Item.Text ?? "");
                e.Handled = true;
                return;
            case "Pin":
                _itemStore.SetPinned(card.Id, !card.Pinned);
                break;
            case "Delete":
                _itemStore.Remove(card.Id);
                break;
        }

        // Persist immediately, same rationale as ShelfViewModel.RemoveSelected: a per-card
        // pin/delete has no other save point before exit, and a crash must not resurrect a
        // deleted item or lose a pin.
        _itemStore.Save();

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
        _flyoutShownAtPressId = null;

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

        // Latched here, before anything touches the mouse. Taking the gesture capture below moves
        // the mouse away from the flyout's PopupRoot, and a StaysOpen="False" Popup reads that as
        // an outside click and closes itself -- so by the time the release runs, "is the flyout
        // showing this card?" always answers false and the toggle would re-open the very flyout
        // the press just dismissed. Measured: the live probe's close-on-second-click check failed
        // exactly that way before this existed.
        _flyoutShownAtPressId = _stackFlyout is not null && _stackFlyout.IsShowing(card.Id) ? card.Id : null;

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

        // Multi-select (Task 15): Ctrl+click always toggles selection instead of the card's normal
        // click action (re-copy / open-link / expand-stack), and once at least one card IS selected
        // a plain click toggles too -- entering selection mode turns every subsequent click into a
        // selection gesture until the user explicitly clears it. Checked before the Link/stack
        // branches below so a stack's Ctrl+click selects rather than opening its flyout.
        var ctrlHeld = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        if (ctrlHeld || (_shelfViewModel?.SelectionMode ?? false))
        {
            _shelfViewModel?.ToggleSelected(card.Id);
            e.Handled = true;
            return;
        }

        if (card.Kind == ClipKind.Link && IsWithinLinkOpenArea(e.OriginalSource))
        {
            OpenLink(card);
        }
        else if (card.IsStack)
        {
            // A stack's click expands it instead of re-copying: the whole point of the flyout is
            // that the individual files inside are separately clickable and draggable, and the
            // stack as a whole is still available by dragging the card itself.
            ToggleStackFlyout(card, CardRootFrom(e.OriginalSource) ?? (UIElement)this);
        }
        else
        {
            CopyCardToClipboard(card);
        }

        e.Handled = true;
    }

    /// <summary>
    /// Runs the outbound drag with the retract countdown suspended for its whole duration, then --
    /// for a stack card only -- decides whether the release was an edge-drag extract (v1.2 Task B).
    /// <para><see cref="DragDropSource.TryStartDrag(FrameworkElement, ClipItem, string, out DragDropEffects)"/>
    /// blocks (it pumps its own message loop), so everything after it runs only once the drop has
    /// completed or been cancelled.</para>
    /// <para>The extract is the SAME gesture the flyout's rows already have -- released nowhere
    /// (<see cref="DragDropEffects.None"/>) with the cursor in the edge band -- decided by the same
    /// <see cref="StackGestures.ShouldSplit"/> against the same <see cref="IsCursorInSplitZone"/>,
    /// and carried out by the same <see cref="OnStackSplitRequested"/>. Dragging the CARD extracts
    /// its FIRST path only; the rest stays stacked, so repeating the gesture peels the stack apart
    /// one file at a time.</para>
    /// <para>The one thing that differs from a row is the band WIDTH -- an argument to that same
    /// hit test, not a second rule: <see cref="StackGestures.CardExtractEdgeBandDip"/> (24) rather
    /// than the row's 60, because a card starts its drag already sitting on the edge-flush shelf.
    /// See that constant.</para>
    /// <para>KNOWN CAVEAT, identical to (and inherited from) the flyout's row split: cancelling the
    /// drag with Esc while the cursor happens to be in the edge band also returns None and so also
    /// extracts. Accepted as-is rather than papered over with a second, different rule.</para>
    /// </summary>
    private void BeginCardDrag(FrameworkElement source, CardViewModel card)
    {
        // Captured BEFORE the blocking drag, exactly as StackFlyout.BeginRowDrag captures its
        // (stack, path) pair: the card list is rebuilt by any store change that lands while
        // DoDragDrop pumps its own message loop, so `card` may be pointing at a detached view model
        // by the time this returns. Null for anything that is not a stack -- a lone Files card, a
        // text/link/image card -- which is what keeps the extract off every other card kind.
        var stackId = card.IsStack ? card.Id : null;
        var firstPath = card.IsStack ? card.Item.Paths[0] : null;

        _isDragging = true;
        _retractTimer.Stop();

        var effect = DragDropEffects.None;

        try
        {
            if (!DragDropSource.TryStartDrag(source, card.Item, BlobsDir, out effect))
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

        if (stackId is null || firstPath is null)
        {
            return;
        }

        var inZone = IsCursorInSplitZone(StackGestures.CardExtractEdgeBandDip);
        if (!StackGestures.ShouldSplit(effect, inZone))
        {
            // No path, no filename -- just the reason half of ShouldSplit failed on, per the
            // project's no-clipboard-content-in-logs rule. See StackGestures.SplitRefusalReason.
            FileLogger.Instance?.Info(Module, StackGestures.SplitRefusalReason(effect, inZone));
            return;
        }

        OnStackSplitRequested(stackId, firstPath);
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

        // The drag this shelf may have been opened FOR has now arrived: _isDragOver holds the
        // shelf from here on, so hand the job back to it. Left running, the grace would keep the
        // shelf out for the remainder of its 3 s even after the drag wandered off again.
        _dragOpenGraceUntil = DateTime.MinValue;

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

    // ---- Stack UX (Task 14): flyout, split, merge ------------------------------------------

    /// <summary>
    /// The one stack flyout, created on first use and reused for every stack afterwards. One
    /// instance rather than one per card: only one can be open at a time by design, and a Popup
    /// owns a top-level window.
    /// </summary>
    private StackFlyout EnsureStackFlyout()
    {
        if (_stackFlyout is not null)
        {
            return _stackFlyout;
        }

        var flyout = new StackFlyout
        {
            // The flyout knows nothing about monitors; this is the only place the resolved work
            // area, its DPI scale, the placed shelf rect and the configured edge all exist
            // together. Both probes read one cursor conversion (CursorDip).
            CursorInSplitZone = () => IsCursorInSplitZone(StackGestures.SplitEdgeBandDip),
            CursorOverShelf = IsCursorOverShelf,
        };

        flyout.FileActivated += OnStackFileActivated;
        flyout.SplitRequested += OnStackSplitRequested;
        flyout.UngroupAllRequested += OnStackUngroupAllRequested;
        flyout.ContentMissing += OnStackContentMissing;

        // A row drag blocks in DoDragDrop exactly like a card drag, and can close the popup out
        // from under itself on the way -- so the retract countdown needs the same explicit
        // suspension a card drag gets rather than relying on the flyout still being open.
        flyout.RowDragStarted += OnStackRowDragStarted;
        flyout.RowDragEnded += OnStackRowDragEnded;

        // Closing (outside click, the flyout's own hover timeout, a stale stack) drops the
        // IsPointerInside term that was suppressing the countdown; nothing else would re-arm it.
        flyout.Closed += (_, _) => ArmRetractIfPointerOutside();

        _stackFlyout = flyout;
        return flyout;
    }

    /// <summary>Opens the flyout for this stack, or closes it if it is already the one on show.</summary>
    private void ToggleStackFlyout(CardViewModel card, UIElement placementTarget)
    {
        var flyout = EnsureStackFlyout();

        // Either answer means "this click is the close half of the toggle": the flyout is still
        // open for this card, or it was when the press landed and the capture change has since
        // closed it (see the latch in OnCardPreviewMouseLeftButtonDown). The latch is compared by
        // Id, so a latch taken on a different card can never swallow this one's open.
        if (string.Equals(_flyoutShownAtPressId, card.Id, StringComparison.Ordinal) ||
            flyout.IsShowing(card.Id))
        {
            flyout.IsOpen = false;
            return;
        }

        // Closed first, then re-opened, so placement is computed from scratch against the new card
        // rather than relying on an already-open Popup re-measuring itself when PlacementTarget
        // changes underneath it (untested either way -- this costs nothing and needs no such
        // assumption).
        flyout.IsOpen = false;
        flyout.ShowFor(card, placementTarget);
    }

    /// <summary>
    /// Points an open flyout back at the live container for its stack after a card-list rebuild,
    /// or closes it when that stack is no longer on screen (filtered out, or scrolled out of the
    /// virtualized list). A flyout hanging off a container that now shows someone else's card is
    /// worse than no flyout: it claims those files belong to the card it is touching.
    /// </summary>
    private void ReanchorStackFlyout()
    {
        var flyout = _stackFlyout;
        if (flyout is null || !flyout.IsOpen)
        {
            return;
        }

        var border = FindLiveCardRoot(flyout.StackId);
        if (border is null || border.DataContext is not CardViewModel card)
        {
            flyout.IsOpen = false;
            return;
        }

        if (ReferenceEquals(flyout.PlacementTarget, border))
        {
            return;
        }

        // Re-shown rather than re-pointed, for the same reason as in ToggleStackFlyout. The rows
        // are rebuilt from the same unchanged paths (CloseIfStale ran first), so the only visible
        // difference is that the flyout is next to the right card again.
        flyout.IsOpen = false;
        flyout.ShowFor(card, border);
    }

    /// <summary>The realized CardRoot Border currently showing the given item, or null.</summary>
    private Border? FindLiveCardRoot(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        foreach (var host in new ItemsControl[] { CardsList, PinnedList })
        {
            var hit = FindCardRootIn(host, id);
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }

    private static Border? FindCardRootIn(DependencyObject parent, string id)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            if (child is Border { Name: CardRootName } border &&
                border.DataContext is CardViewModel card &&
                string.Equals(card.Id, id, StringComparison.Ordinal))
            {
                return border;
            }

            var found = FindCardRootIn(child, id);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private void CloseStackFlyout()
    {
        if (_stackFlyout is not null)
        {
            _stackFlyout.IsOpen = false;
        }
    }

    /// <summary>
    /// The mouse cursor in the same DIP space as <see cref="_area"/> and <see cref="_rect"/>, or
    /// null when it cannot be read (or the shelf has never been placed, so there is no space to
    /// express it in).
    ///
    /// <para>ONE conversion feeding every cursor-based question the stack UX asks -- the split
    /// band and "is the pointer on the shelf?" are two readings of the same position and must not
    /// be derived by two different sums. The cursor comes back in physical pixels and the geometry
    /// is in DIPs, so it is divided by the very scale <see cref="MonitorGeometry.Resolve"/> used to
    /// produce that geometry; converted with any other number the answers land in the wrong place
    /// on a scaled display.</para>
    /// </summary>
    private (double X, double Y)? CursorDip()
    {
        if (!_placed)
        {
            return null;
        }

        try
        {
            return MonitorGeometry.CursorDip(_area);
        }
        catch (Exception ex)
        {
            // Reading the cursor is a Win32 call; a failure here must degrade to "don't know",
            // never crash at the end of a drag or inside a timer tick.
            FileLogger.Instance?.Warn(Module, $"could not read the cursor position: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// True when the cursor is, right now, inside the split band along the configured screen edge.
    /// Called once per drag, immediately after that drag returns.
    /// <para>ONE hit test for both gestures, differing only in <paramref name="bandDip"/>: a flyout
    /// row passes <see cref="StackGestures.SplitEdgeBandDip"/>, a stack card passes the narrower
    /// <see cref="StackGestures.CardExtractEdgeBandDip"/> (see those constants for why). Required,
    /// not defaulted, so neither caller can silently inherit the other's width.</para>
    /// </summary>
    private bool IsCursorInSplitZone(double bandDip)
    {
        if (CursorDip() is not { } cursor)
        {
            return false;
        }

        return StackGestures.IsInSplitZone(
            new ShelfPlacement.Rect(_area.X, _area.Y, _area.W, _area.H),
            _edge, cursor.X, cursor.Y, bandDip);
    }

    /// <summary>
    /// True when the cursor is over the shelf's placed rectangle. The flyout's auto-close asks this
    /// instead of reading <see cref="UIElement.IsMouseOver"/>.
    ///
    /// <para>That is not a stylistic choice. While the flyout is open it holds a
    /// <c>StaysOpen="False"</c> Popup's SubTree mouse capture -- the same mechanism that closes the
    /// popup when the shelf takes the gesture capture (see the latch in
    /// <see cref="OnCardPreviewMouseLeftButtonDown"/>) -- and WPF then reports this window as NOT
    /// moused-over even with the pointer parked on the very card the flyout belongs to. An
    /// IsMouseOver term would therefore be dead the whole time it mattered, and the flyout would
    /// close itself under a stationary cursor about a second after opening.</para>
    /// </summary>
    private bool IsCursorOverShelf()
    {
        if (!IsVisible || _slidingOut)
        {
            return false;
        }

        return CursorDip() is { } cursor && StackGestures.Contains(_rect, cursor.X, cursor.Y);
    }

    /// <summary>A flyout row was clicked: put that one file on the clipboard.</summary>
    private void OnStackFileActivated(string path)
    {
        // Re-checked at action time rather than trusting the row's snapshot: the flyout may have
        // been open for a while. Same existence rule the drag payload uses.
        var paths = DragDropSource.PathExists(path) ? new[] { path } : Array.Empty<string>();

        if (!SetClipboardFiles(paths))
        {
            FileLogger.Instance?.Warn(Module, "copy of a stacked file found nothing left on disk");
            ShowStatus(Strings.FileMissing);
            return;
        }

        ConfirmCopy();
    }

    /// <summary>A flyout row was dragged into the edge band: pull it out into its own card.</summary>
    private void OnStackSplitRequested(string stackId, string path)
    {
        if (_itemStore is null)
        {
            return;
        }

        if (_itemStore.SplitFile(stackId, path) is null)
        {
            // The stack changed under the drag (another capture merged it, the file was removed).
            // Nothing to tell the user: no card moved, and the drop itself did nothing either.
            FileLogger.Instance?.Warn(Module, "split refused: the path is no longer part of that stack");
            return;
        }

        _itemStore.Save();
    }

    /// <summary>
    /// The flyout header's "ungroup all" button (v1.3 Task C): the explicit-UI primary path onto
    /// <see cref="ItemStore.SplitAll"/>, replacing the hidden edge-band drag as the way most users
    /// are expected to discover ungrouping. No zone check, no reason-code log -- unlike the drag
    /// refusal path, a button click can only ever mean "yes, ungroup", so there is nothing to
    /// refuse silently and nothing worth logging.
    /// </summary>
    private void OnStackUngroupAllRequested(string stackId)
    {
        if (_itemStore is null)
        {
            return;
        }

        if (_itemStore.SplitAll(stackId) is null)
        {
            // The stack changed under the click (another capture merged it, or it was removed) --
            // same "nothing to tell the user" reasoning as OnStackSplitRequested's own refusal.
            FileLogger.Instance?.Warn(Module, "ungroup-all refused: the stack no longer exists");
            return;
        }

        _itemStore.Save();
    }

    private void OnStackContentMissing() => ShowStatus(Strings.FileMissing);

    private void OnStackRowDragStarted()
    {
        _isDragging = true;
        _retractTimer.Stop();
    }

    private void OnStackRowDragEnded()
    {
        _isDragging = false;

        // The pointer is wherever the drop left it, and the MouseLeave was consumed mid-drag.
        _pointerInside = IsMouseOver;
        ArmRetractIfPointerOutside();
    }

    /// <summary>
    /// DragEnter/DragOver on a card. Handles -- and marks handled, keeping the window-level
    /// drag-IN handlers out of it -- only a whole-card drag that this card could actually absorb.
    /// Everything else (an external file drop, a flyout row, a card onto itself, a non-Files
    /// combination) is left to bubble to the window, which answers it exactly as it did before.
    /// </summary>
    private void OnCardDragOver(object sender, DragEventArgs e)
    {
        var merge = ResolveMerge(e);
        if (merge is null)
        {
            ClearMergeHighlight();
            return;
        }

        // The window's DragEnter never runs for a merge, so its retract suspension has to happen
        // here instead -- an OLE drag produces no MouseMove, so nothing else would hold the shelf.
        _isDragOver = true;
        _retractTimer.Stop();

        HighlightMergeTarget(CardRootFrom(e.OriginalSource));

        // WPF requires Effects to be re-set on every DragOver, not just on DragEnter.
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    /// <summary>
    /// The drag left this card. Deliberately does NOT mark the event handled: the window-level
    /// DragLeave still has to clear <see cref="_isDragOver"/> and re-arm the countdown for a drag
    /// that has left the shelf altogether. Moving from one card to the next clears the highlight
    /// here and the next DragEnter sets it again a frame later.
    /// </summary>
    private void OnCardDragLeave(object sender, DragEventArgs e) => ClearMergeHighlight();

    /// <summary>
    /// The merge itself. A refusal (<see cref="ItemStore.TryMergeFiles"/> false -- the combined
    /// stack would exceed 10 files) is shown rather than swallowed: the target card shakes and the
    /// footer says why, because from the user's side a silently ignored drop is indistinguishable
    /// from a missed one.
    ///
    /// <para>v1.3 Task B: either side of the pair may be a clipboard screenshot (Kind==Image) --
    /// <see cref="StackGestures.CanAcceptMerge"/> now admits that combination.
    /// <see cref="DragDropSource.TryPrepareCardsForMerge"/> converts whichever side(s) need it to
    /// Kind==Files BEFORE the same <see cref="ItemStore.TryMergeFiles"/> this always used runs --
    /// see its remarks for why both sides are resolved before either is mutated (a healthy target
    /// must never end up silently converted just because the OTHER side's blob turned out to be
    /// missing). A conversion refusal shakes and reports the same way the cap refusal does, since
    /// from the user's side both are "the drop did not go through".</para>
    /// </summary>
    private void OnCardDrop(object sender, DragEventArgs e)
    {
        var merge = ResolveMerge(e);
        var targetBorder = CardRootFrom(e.OriginalSource);

        ClearMergeHighlight();

        if (merge is null || _itemStore is null)
        {
            return;
        }

        _isDragOver = false;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;

        var (target, source) = merge.Value;

        if (!DragDropSource.TryPrepareCardsForMerge(_itemStore, BlobsDir, target, source))
        {
            FileLogger.Instance?.Info(Module, "merge refused: image content missing");
            ShowStatus(Strings.FileMissing);
            ShakeCard(targetBorder);
            ArmRetractIfPointerOutside();
            return;
        }

        if (_itemStore.TryMergeFiles(target.Id, source.Id))
        {
            _itemStore.Save();
        }
        else
        {
            FileLogger.Instance?.Info(Module, "merge refused: the combined stack would exceed 10 files");
            ShowStatus(Strings.StackLimit);
            ShakeCard(targetBorder);

            // TryPrepareCardsForMerge above already ran, unconditionally, before the 10-file cap
            // check -- it may have converted an Image card to Files in memory AND renamed its
            // blob / deleted its thumb ON DISK (see its own remarks). That conversion is NOT
            // undone just because the merge itself is then refused: the cap check only decides
            // whether the two cards combine, not whether the conversion prep stands. Without this
            // Save(), items.dat stays stale (still Kind=Image, still pointing at a thumb file that
            // no longer exists) until the next unrelated save -- and a crash in that window would
            // resurrect the card from the stale on-disk record, broken. Same persistence-on-
            // irreversible-mutation rationale as OnCardActionClick's pin/delete and
            // ShelfViewModel.RemoveSelected.
            _itemStore.Save();
        }

        ArmRetractIfPointerOutside();
    }

    /// <summary>
    /// The (target, source) pair a drag over a card would merge, or null when this drag is not an
    /// acceptable merge. One resolution for both halves: the highlight, the drop effect and the
    /// store call all come from this single answer, so they cannot contradict each other.
    /// </summary>
    private (ClipItem Target, ClipItem Source)? ResolveMerge(DragEventArgs e)
    {
        if (_itemStore is null || !DragDropTarget.IsCardMergeDrag(e.Data))
        {
            return null;
        }

        var targetCard = CardFrom(e.OriginalSource);
        if (targetCard is null)
        {
            return null;
        }

        var sourceId = DragDropTarget.SourceCardId(e.Data);
        if (string.IsNullOrEmpty(sourceId))
        {
            return null;
        }

        var source = _itemStore.Items.FirstOrDefault(i => i.Id == sourceId);
        return StackGestures.CanAcceptMerge(targetCard.Item, source)
            ? (targetCard.Item, source!)
            : null;
    }

    private void HighlightMergeTarget(Border? border)
    {
        if (ReferenceEquals(_mergeHighlight, border))
        {
            return;
        }

        ClearMergeHighlight();

        if (border is null)
        {
            return;
        }

        border.BorderBrush = MergeAcceptBorderBrush;
        _mergeHighlight = border;
    }

    private void ClearMergeHighlight()
    {
        if (_mergeHighlight is null)
        {
            return;
        }

        // Assigned back explicitly rather than ClearValue: the brush came from a value the
        // DataTemplate set on this instance, and clearing it would not restore that value.
        _mergeHighlight.BorderBrush = CardIdleBorderBrush;
        _mergeHighlight = null;
    }

    /// <summary>
    /// The refusal shake: a short damped horizontal wobble on the target card.
    /// <para>FillBehavior.Stop and a final 0 keyframe both matter -- the Border lives in a
    /// virtualized, recycled container, so a transform left holding a non-zero offset would come
    /// back applied to whatever card that container is reused for.</para>
    /// </summary>
    private static void ShakeCard(Border? border)
    {
        if (border is null)
        {
            return;
        }

        if (border.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            border.RenderTransform = transform;
        }

        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = ShakeDuration,
            FillBehavior = FillBehavior.Stop,
        };

        double[] offsets = { -8, 8, -5, 5, -2, 2, 0 };
        var step = ShakeDuration.TimeSpan.TotalMilliseconds / offsets.Length;

        for (var i = 0; i < offsets.Length; i++)
        {
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(
                offsets[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(step * (i + 1)))));
        }

        transform.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    /// <summary>
    /// Click-to-copy: puts the card back on the clipboard without TNDrop re-capturing its own
    /// write, optionally floats the item to the top of the shelf, and confirms with the same
    /// indicator flash + sound a real capture produces.
    /// </summary>
    private void CopyCardToClipboard(CardViewModel card)
    {
        var item = card.Item;

        var copied = false;
        switch (item.Kind)
        {
            case ClipKind.Text:
            case ClipKind.Link:
                if (!string.IsNullOrEmpty(item.Text))
                {
                    // Before the write, not after: the clipboard notification can arrive before
                    // SetX returns.
                    global::TNDrop.App.Monitor?.SuppressNext();
                    ClipboardIo.SetText(item.Text);
                    copied = true;
                }

                break;

            case ClipKind.Files:
                // Same resolution the drag payload uses, so a click and a drag of the same card
                // can never disagree about which of its paths still exist.
                copied = SetClipboardFiles(DragDropSource.ExistingPaths(item));
                break;

            case ClipKind.Image:
                var image = DragDropSource.LoadImage(item, BlobsDir);
                if (image is not null)
                {
                    global::TNDrop.App.Monitor?.SuppressNext();
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

        ConfirmCopy();

        // Last, after the clipboard write has already returned (ClipboardIo.SetText/SetFiles/
        // SetImage are synchronous, retries included): the keystroke must never overtake the
        // content it is supposed to paste.
        TryPasteOnClick(item.Kind);
    }

    /// <summary>
    /// Click-to-paste (v1.2 Task H): after a click has re-copied a Text/Link card, send Ctrl+V so
    /// the content lands in the app the user is working in without them reaching for the keyboard.
    /// <para>Whether to do it at all is <see cref="ClickPaste.ShouldPasteOnClick"/>'s single
    /// answer -- this method only collects the five inputs and acts on the verdict. The three
    /// gestures that must NOT paste but still re-copy are already excluded before
    /// <see cref="CopyCardToClipboard"/> is ever reached (Ctrl+click and selection mode return
    /// early in <see cref="OnCardPreviewMouseLeftButtonUp"/>, the Link hotspot branches to
    /// <see cref="OpenLink"/>), so nothing re-tests them here.</para>
    /// <para>The paste target is whatever holds the foreground, which is the user's app rather than
    /// the shelf precisely because the shelf is WS_EX_NOACTIVATE (see
    /// <see cref="OnSourceInitialized"/>). That is also why the guards matter: the one moment the
    /// shelf DOES hold activation is the search box's deliberate exception, and pasting then would
    /// type into our own search field.</para>
    /// </summary>
    private void TryPasteOnClick(ClipKind kind)
    {
        // Named arguments: four of the five parameters are bools, and a transposition here (say,
        // foreground where focus belongs) would compile, pass every test that exercises the
        // predicate directly, and only show up as a keystroke in the wrong window.
        //
        // Resolve, not ShouldPasteOnClick: the suppression-reason log below (Task F, v1.3) has to
        // read off the SAME switch that decides whether to paste, not a second expression that
        // could quietly drift from it -- see ClickPasteResult's own doc comment.
        var result = ClickPaste.Resolve(
            kind: kind,
            pasteOnClickSetting: global::TNDrop.App.Settings?.PasteOnClick ?? false,
            ownProcessForeground: InputSender.IsOwnProcessForeground(),
            keyboardFocusWithin: IsKeyboardFocusWithin,
            modifiersDown: InputSender.AnyModifierDown());

        if (result != ClickPasteResult.Paste)
        {
            // Reason code only -- no path, no clipboard content (per the project's logging rule).
            // Null for NotApplicable (setting off, or a Files/Image card): nothing was ever going
            // to paste, so there is nothing worth a log line about.
            var reason = ClickPaste.SuppressReasonCode(result);
            if (reason is not null)
            {
                FileLogger.Instance?.Info(Module, $"paste suppressed: {reason}");
            }

            return;
        }

        InputSender.SendCtrlV();
    }

    /// <summary>
    /// Puts file paths on the clipboard without TNDrop re-capturing its own write. False when
    /// there is nothing left to write, so the caller can report it. Shared by the whole-card click
    /// and the stack flyout's single-row click, so the two cannot drift apart on suppression.
    /// </summary>
    private static bool SetClipboardFiles(string[] paths)
    {
        if (paths.Length == 0)
        {
            return false;
        }

        // Before the write, not after: the clipboard notification can arrive before SetFiles returns.
        global::TNDrop.App.Monitor?.SuppressNext();
        ClipboardIo.SetFiles(paths);
        return true;
    }

    /// <summary>
    /// The "it's on the clipboard" confirmation: the same indicator flash and capture sound a real
    /// clipboard capture produces, so re-copying is indistinguishable from capturing.
    /// </summary>
    private static void ConfirmCopy()
    {
        var settings = global::TNDrop.App.Settings;
        if (settings is not null)
        {
            global::TNDrop.App.FlashIndicator(settings.IndicatorStyle, settings.Edge);
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
    /// The header's "?" button (v1.3.1): opens the README.html bundled next to the exe
    /// (assets/README.html at build time, copied to the publish output root by
    /// TNDrop.App.csproj -- see that file's Content item) in the user's default browser.
    /// <para>The file-exists check is split out into <see cref="HelpLauncher.MissingReasonCode"/>
    /// so that decision is unit-testable without touching the real filesystem or spawning a
    /// process; this method only resolves the path, asks the pure helper, and does the I/O.</para>
    /// <para>Missing file or a failed <see cref="Process.Start"/> (no default browser associated,
    /// shell launch refused, etc.) never crashes the shelf: both log a single Warn line with a
    /// reason code only -- no path, per the project's no-clipboard-content/no-paths-in-logs rule
    /// (see <see cref="StackGestures.SplitRefusalReason"/> for the same convention elsewhere in
    /// this class) -- and fall back to the same inline footer status <see cref="ShowStatus"/>
    /// every other transient failure in this window already uses.</para>
    /// </summary>
    private void OnHelpButtonClick()
    {
        var path = Path.Combine(AppContext.BaseDirectory, HelpLauncher.ReadmeFileName);

        var missingReason = HelpLauncher.MissingReasonCode(File.Exists(path));
        if (missingReason is not null)
        {
            FileLogger.Instance?.Warn(Module, $"help open failed: {missingReason}");
            ShowStatus(Strings.HelpOpenFailed);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception)
        {
            FileLogger.Instance?.Warn(Module, "help open failed: start-error");
            ShowStatus(Strings.HelpOpenFailed);
        }
    }

    private void OnPinButtonClick()
    {
        _pinned = !_pinned;
        global::TNDrop.App.SetShelfPinned(_pinned);
        UpdatePinButtonVisual();

        if (_pinned)
        {
            _retractTimer.Stop();
        }
        else
        {
            ArmRetractIfPointerOutside();
        }
    }

    /// <summary>ピンボタンのオン/オフ表示 (v1.5 追補)。ピン中はグリフを E840 (pinned) +
    /// アクセント色にし、うっすら固定背景でトグルのオン状態を常時見せる。ホバー時は
    /// Header.IconButtonStyle のテンプレートトリガーが背景を上書きするので、ここで設定する
    /// Background は非ホバー時の土台だけを受け持つ。</summary>
    private void UpdatePinButtonVisual()
    {
        HeaderPinButton.ToolTip = _pinned ? Strings.HeaderPinActiveTooltip : Strings.HeaderPinTooltip;
        HeaderPinIcon.Text = _pinned ? "\uE840" : "\uE718";
        HeaderPinIcon.Foreground = _pinned
            ? (Brush)FindResource("Card.SelectedAccent")
            : (Brush)FindResource("Card.Foreground");
        HeaderPinButton.Background = _pinned
            ? new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF))
            : System.Windows.Media.Brushes.Transparent;
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

        // The two share one footer cell (see ShelfWindow.xaml): the count line has to make way
        // for the transient message rather than show through underneath it.
        CountText.Visibility = Visibility.Collapsed;

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
        CountText.Visibility = Visibility.Visible;
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

    /// <summary>
    /// The outermost Border of the card a hit-test source belongs to -- the element that carries
    /// the merge highlight and the refusal shake -- or null when the source is not on a card.
    /// Matched by name rather than by type because a card's sub-tree contains several other
    /// Borders (the stack-layer glyphs, the count badge) that would otherwise be found first.
    /// </summary>
    private static Border? CardRootFrom(object? source)
    {
        for (var current = source as DependencyObject; current is not null; current = ParentOf(current))
        {
            if (current is Border { Name: CardRootName } border)
            {
                return border;
            }

            if (current is ItemsControl)
            {
                break;
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

        // v1.4 review fix I1: this prompt used to count CARDS (_shelfViewModel.Cards.Count), so
        // confirming the deletion of one 3-file stack read "1件" when 3 files were about to be
        // removed. ClearVisibleFileCount counts FILES via the same Contribution weighting the
        // footer uses, scoped to exactly what ClearVisible() deletes (Cards only, not pinned).
        var fileCount = _shelfViewModel.ClearVisibleFileCount;
        if (fileCount == 0)
        {
            return;
        }

        var message = string.Format(CultureInfo.CurrentUICulture, Strings.ClearConfirmMessageFormat, fileCount);
        var result = MessageBox.Show(this, message, Strings.ClearConfirmTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            _shelfViewModel.ClearVisible();
        }
    }

    /// <summary>
    /// "選択をコピー": text-only selection joins with newlines into SetText; files/stacks-only
    /// combines every path into one SetFiles; a mix of the two copies the files only (files take
    /// priority over text) and adds a transient "N files copied" note, since silently dropping the
    /// text half would otherwise look like nothing happened to it. Image cards in the selection are
    /// not carried by either path -- there is no brief-specified way to combine an image with text
    /// or file paths on the clipboard.
    /// </summary>
    private void OnCopySelectedClick(object sender, RoutedEventArgs e)
    {
        if (_shelfViewModel is null)
        {
            return;
        }

        var items = _shelfViewModel.GetSelectedItems();
        if (items.Count == 0)
        {
            return;
        }

        var fileItems = items.Where(i => i.Kind == ClipKind.Files).ToList();
        var textItems = items.Where(i => i.Kind == ClipKind.Text || i.Kind == ClipKind.Link).ToList();
        var mixed = fileItems.Count > 0 && fileItems.Count < items.Count;

        bool copied;
        if (fileItems.Count > 0)
        {
            var paths = fileItems.SelectMany(DragDropSource.ExistingPaths).ToArray();
            copied = SetClipboardFiles(paths);

            if (copied && mixed)
            {
                ShowStatus(string.Format(CultureInfo.CurrentUICulture, Strings.FilesCopiedFormat, paths.Length));
            }
        }
        else if (textItems.Count > 0)
        {
            var text = string.Join(Environment.NewLine, textItems.Select(i => i.Text ?? string.Empty));

            // Before the write, not after: the clipboard notification can arrive before SetText returns.
            global::TNDrop.App.Monitor?.SuppressNext();
            ClipboardIo.SetText(text);
            copied = true;
        }
        else
        {
            // Selection is images only (or empty after filtering): nothing this bar knows how to
            // combine onto one clipboard payload.
            copied = false;
        }

        if (!copied)
        {
            ShowStatus(Strings.FileMissing);
            return;
        }

        ConfirmCopy();
    }

    /// <summary>
    /// "選択を削除": no confirmation dialog -- the batch bar itself is the explicit step, unlike the
    /// footer's "Clear" which can silently wipe an entire filtered view. Confirmed instead with the
    /// same flash the footer's Clear and click-to-copy already use, plus the delete sound.
    /// </summary>
    private void OnDeleteSelectedClick(object sender, RoutedEventArgs e)
    {
        if (_shelfViewModel is null || _shelfViewModel.SelectedCount == 0)
        {
            return;
        }

        _shelfViewModel.RemoveSelected();
        ConfirmDelete();
    }

    /// <summary>The delete counterpart to <see cref="ConfirmCopy"/>: same indicator flash, the
    /// delete sound instead of the capture one.</summary>
    private static void ConfirmDelete()
    {
        var settings = global::TNDrop.App.Settings;
        if (settings is not null)
        {
            global::TNDrop.App.FlashIndicator(settings.IndicatorStyle, settings.Edge);
        }

        global::TNDrop.App.Sounds?.PlayDelete();
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
        // Before the scroll restore and before its early return: the flyout's rows are a snapshot
        // of a stack that may have just been removed, merged into another card, or split.
        _stackFlyout?.CloseIfStale(_itemStore);

        // A merge target that was mid-shake is gone from the tree with the rebuild, and the
        // highlighted Border is now a recycled container that may host a different card.
        ClearMergeHighlight();

        // Same hazard for a flyout that survived the staleness check: Cards was Clear()-and-
        // repopulated, so the container it is anchored to has been regenerated and -- with
        // recycling on -- very likely now hosts a DIFFERENT card. Deferred to Loaded priority
        // because the new containers only exist after the layout pass that follows, exactly like
        // the scroll restore below.
        if (_stackFlyout is not null && _stackFlyout.IsOpen)
        {
            Dispatcher.BeginInvoke(new Action(ReanchorStackFlyout), DispatcherPriority.Loaded);
        }

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
