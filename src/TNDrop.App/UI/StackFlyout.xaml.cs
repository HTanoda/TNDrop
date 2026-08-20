using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using TNDrop.Core;
using TNDrop.Platform;
using TNDrop.Services;
using DragDrop = System.Windows.DragDrop;
using DragDropEffects = System.Windows.DragDropEffects;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseEventHandler = System.Windows.Input.MouseEventHandler;
using Point = System.Windows.Point;

namespace TNDrop.UI;

/// <summary>
/// The expanded view of a Files stack: one row per path, opened by clicking the stack card.
///
/// <para>The flyout itself owns no state beyond "which stack am I showing". Everything with a
/// consequence -- putting a file on the clipboard, splitting one out of the stack -- is raised as
/// an event and carried out by ShelfWindow, which already owns the store and the clipboard
/// confirmation. That keeps a single copy of those rules rather than a second one in here.</para>
/// </summary>
public partial class StackFlyout : Popup
{
    private const string Module = "StackFlyout";

    /// <summary>
    /// How often the flyout checks whether the pointer is still on it or on the shelf. See
    /// <see cref="OnHoverTick"/> for why it polls rather than listening for MouseLeave.
    /// </summary>
    private static readonly TimeSpan HoverPollInterval = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Consecutive misses before the flyout closes itself. Two, not one: the flyout is offset a few
    /// pixels away from the card, and a pointer crossing that gap must not be read as "left".
    /// </summary>
    private const int HoverMissesBeforeClose = 2;

    private readonly DispatcherTimer _hoverTimer;

    private ClipItem? _stackItem;
    private List<string> _paths = new();
    private Window? _owner;
    private int _hoverMisses;

    // Row press/drag classification, the same shape as ShelfWindow's card gesture: a press records
    // where and on what, a move past the system threshold promotes it to a drag, a release before
    // that is a click.
    private Point _pressPoint;
    private StackFileRow? _pressedRow;
    private UIElement? _captureHost;
    private bool _isDragging;

    public StackFlyout()
    {
        InitializeComponent();

        _hoverTimer = new DispatcherTimer { Interval = HoverPollInterval };
        _hoverTimer.Tick += OnHoverTick;

        Opened += OnOpened;
        Closed += OnClosed;

        // Wired once on the host, not per row: the row visual is a DataTemplate with no code-behind
        // and its containers are regenerated every time the flyout opens.
        RowsHost.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnRowPreviewMouseLeftButtonDown), true);
        RowsHost.AddHandler(UIElement.MouseMoveEvent,
            new MouseEventHandler(OnRowMouseMove), true);
        RowsHost.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler(OnRowPreviewMouseLeftButtonUp), true);
        RowsHost.LostMouseCapture += OnRowsHostLostMouseCapture;
    }

    /// <summary>Id of the stack currently on show, or null when the flyout has never been opened.</summary>
    public string? StackId { get; private set; }

    /// <summary>
    /// Answers "is the cursor in the screen-edge split band right now?" at the moment a row drag
    /// ends. Supplied by ShelfWindow, which is the only thing that knows the resolved monitor work
    /// area, its DPI scale and the configured edge. Null (designer/tests) means no split ever.
    /// </summary>
    public Func<bool>? CursorInSplitZone { get; set; }

    /// <summary>A row was clicked: put that one file on the clipboard. Argument is the full path.</summary>
    public event Action<string>? FileActivated;

    /// <summary>A row was dragged into the edge band and refused everywhere else: split it out.</summary>
    public event Action<string, string>? SplitRequested;

    /// <summary>A row could not be acted on because its file is gone from disk.</summary>
    public event Action? ContentMissing;

    /// <summary>Raised around the blocking <see cref="DragDrop.DoDragDrop"/> of a row drag, so the
    /// shelf can suspend its retract countdown for the duration exactly as it does for a card drag.</summary>
    public event Action? RowDragStarted;

    /// <summary>See <see cref="RowDragStarted"/>. Always raised, including when the drag threw.</summary>
    public event Action? RowDragEnded;

    /// <summary>
    /// Opens the flyout against <paramref name="placementTarget"/> (the stack card) showing
    /// <paramref name="stack"/>'s files. Re-reads the paths every time, so a stack that changed
    /// while the flyout was closed shows its current contents.
    /// </summary>
    public void ShowFor(CardViewModel stack, UIElement placementTarget)
    {
        if (stack is null || placementTarget is null)
        {
            return;
        }

        _stackItem = stack.Item;
        StackId = stack.Id;
        _paths = stack.Item.Paths.ToList();

        RowsHost.ItemsSource = _paths.Select(StackFileRow.Create).ToList();

        // Away from the screen edge, never over it: on a left-edge shelf the flyout opens to the
        // right of the card, and vice versa, so it never lands off-screen.
        var edge = global::TNDrop.App.Settings?.Edge ?? EdgeSide.Left;
        Placement = edge == EdgeSide.Left ? PlacementMode.Right : PlacementMode.Left;
        HorizontalOffset = edge == EdgeSide.Left ? 6 : -6;

        PlacementTarget = placementTarget;
        _owner = Window.GetWindow(placementTarget);

        _hoverMisses = 0;
        IsOpen = true;
    }

    /// <summary>True when the flyout is currently showing that exact stack. Drives the click toggle.</summary>
    public bool IsShowing(string? stackId) =>
        IsOpen && stackId is not null && string.Equals(StackId, stackId, StringComparison.Ordinal);

    /// <summary>
    /// Closes the flyout if the stack it is showing has been removed or has changed underneath it.
    /// Called after every store-driven rebuild.
    ///
    /// <para>Closing rather than refreshing is deliberate: the rows the user is looking at are the
    /// thing they are about to click or drag, and silently swapping them for different files
    /// mid-gesture is how the wrong file ends up on the clipboard. A split -- the one change the
    /// flyout itself causes -- is also the one case where the user has clearly finished with the
    /// row they were holding.</para>
    /// </summary>
    public void CloseIfStale(ItemStore? store)
    {
        if (!IsOpen)
        {
            return;
        }

        if (store is null || StackId is null)
        {
            IsOpen = false;
            return;
        }

        var item = store.Items.FirstOrDefault(i => i.Id == StackId);
        if (item is null || item.Kind != ClipKind.Files || !item.Paths.SequenceEqual(_paths))
        {
            IsOpen = false;
            return;
        }

        // Same id, same paths -- but the store hands out snapshots, so keep the live instance the
        // next row drag will validate against rather than the one captured at open time.
        _stackItem = item;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        _hoverMisses = 0;
        _hoverTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _hoverTimer.Stop();
        _pressedRow = null;
        ReleaseGestureCapture();
    }

    /// <summary>
    /// Closes the flyout once the pointer has been off both it and the shelf for a moment.
    ///
    /// <para>Polling, not MouseLeave: the flyout is its own top-level window, so moving from the
    /// card onto the flyout raises MouseLeave on the shelf and moving back raises it on the
    /// flyout -- either one, taken literally, would close the flyout the instant the user reached
    /// for it. Asking "is the pointer on either of us?" a few times a second has no such gap.</para>
    ///
    /// <para>This is what keeps the shelf dismissable at all: the shelf suppresses its retract
    /// countdown while the flyout is open (see ShelfWindow.IsPointerInside), so without a way for
    /// the flyout to close on its own, walking away from an expanded stack would leave the shelf
    /// parked on screen for good.</para>
    /// </summary>
    private void OnHoverTick(object? sender, EventArgs e)
    {
        if (!IsOpen)
        {
            _hoverTimer.Stop();
            return;
        }

        // A drag pumps its own message loop and takes the pointer with it; "not hovering" says
        // nothing about the user's intent while one is in flight.
        if (_isDragging)
        {
            _hoverMisses = 0;
            return;
        }

        var overFlyout = Child is FrameworkElement child && child.IsMouseOver;
        var overShelf = _owner is not null && _owner.IsMouseOver;

        if (overFlyout || overShelf || IsKeyboardFocusWithin)
        {
            _hoverMisses = 0;
            return;
        }

        if (++_hoverMisses >= HoverMissesBeforeClose)
        {
            IsOpen = false;
        }
    }

    private void OnRowPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressedRow = null;

        var row = RowFrom(e.OriginalSource);
        if (row is null)
        {
            return;
        }

        _pressPoint = e.GetPosition(RowsHost);

        // Capture before arming, for the same reason ShelfWindow does: taking the mouse can
        // synthesize a MouseMove immediately, and an already-armed press would be cancelled by it.
        CaptureForGesture(RowsHost);
        _pressedRow = row;
    }

    /// <summary>
    /// Takes the mouse so the drag threshold is still evaluated after the pointer has left the
    /// flyout. Not optional here: the split gesture is "drag the row to the screen edge", which
    /// leaves the flyout within a few pixels -- without capture no further MouseMove would arrive
    /// and the drag would never start at all.
    /// <para>Capturing a DESCENDANT of the popup (rather than something outside it) is what keeps
    /// <c>StaysOpen="False"</c> from treating the capture change as an outside click.</para>
    /// </summary>
    private void CaptureForGesture(UIElement host)
    {
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

    private void OnRowsHostLostMouseCapture(object sender, MouseEventArgs e)
    {
        // Bubbles, so a descendant releasing its own capture reaches here too; only the host's own
        // loss abandons the gesture.
        if (!ReferenceEquals(sender, e.OriginalSource))
        {
            return;
        }

        _captureHost = null;
        _pressedRow = null;
    }

    private void OnRowMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedRow is null || _isDragging)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _pressedRow = null;
            ReleaseGestureCapture();
            return;
        }

        var position = e.GetPosition(RowsHost);
        if (Math.Abs(position.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var row = _pressedRow;
        _pressedRow = null;

        // Hand the mouse over before DoDragDrop, which takes the capture itself.
        ReleaseGestureCapture();

        BeginRowDrag(row);
    }

    private void OnRowPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var row = _pressedRow;
        _pressedRow = null;
        ReleaseGestureCapture();

        if (row is null || _isDragging)
        {
            return;
        }

        e.Handled = true;
        FileActivated?.Invoke(row.Path);
    }

    /// <summary>
    /// Runs the single-file drag and, when it comes back refused, decides whether the release was
    /// a split.
    ///
    /// <para>The (stack, path) pair is captured in locals first: <see cref="DragDrop.DoDragDrop"/>
    /// blocks with its own message loop, and a <c>StaysOpen="False"</c> popup can be closed by the
    /// drag taking the mouse -- so by the time this returns, <see cref="StackId"/> may already have
    /// been cleared and the rows torn down. The split must still go through.</para>
    /// </summary>
    private void BeginRowDrag(StackFileRow row)
    {
        var stackId = StackId;
        var path = row.Path;

        if (string.IsNullOrEmpty(stackId))
        {
            return;
        }

        var data = DragDropSource.BuildStackRowDataObject(_stackItem, path);
        if (data is null)
        {
            ContentMissing?.Invoke();
            return;
        }

        var effect = DragDropEffects.None;

        _isDragging = true;
        RowDragStarted?.Invoke();
        try
        {
            effect = DragDrop.DoDragDrop(RowsHost, data, DragDropEffects.Copy | DragDropEffects.Link);
        }
        catch (Exception ex)
        {
            // A drop target that throws must not take the shelf down. Treated as "went nowhere",
            // which is also what the edge-zone check below expects for a split.
            FileLogger.Instance?.Error(Module, "Row drag failed", ex);
        }
        finally
        {
            _isDragging = false;
            RowDragEnded?.Invoke();
        }

        var inZone = CursorInSplitZone?.Invoke() ?? false;
        if (!StackGestures.ShouldSplit(effect, inZone))
        {
            return;
        }

        SplitRequested?.Invoke(stackId, path);
    }

    /// <summary>The row a hit-test source belongs to, or null when it is not on a row.</summary>
    private static StackFileRow? RowFrom(object? source)
    {
        for (var current = source as DependencyObject; current is not null; current = ParentOf(current))
        {
            if (current is FrameworkElement { DataContext: StackFileRow row })
            {
                return row;
            }

            if (current is ItemsControl)
            {
                break;
            }
        }

        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
            ? System.Windows.Media.VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);
}
