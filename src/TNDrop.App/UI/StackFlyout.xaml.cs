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
using TNDrop.Resources;
using TNDrop.Services;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
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
    private int _hoverMisses;

    /// <summary>
    /// Bumped once per <see cref="ShowFor"/> call (v1.3 Task C review fix). Row thumbnails resolve
    /// on a background thread (see <see cref="ShellImagingWorker"/>) and land some time later; a
    /// late result is applied only when both this generation AND <see cref="StackId"/> still match
    /// what they were when the request was scheduled -- so a flyout that has since closed, or been
    /// re-shown for a different stack (even one that happens to reuse the same StackId, e.g. the
    /// SAME stack closed and reopened), never has a stale background result silently applied to
    /// row objects nobody is looking at anymore. Two conditions, not one: StackId alone would not
    /// catch "same stack, closed and reopened" (a fresh row list, fresh generation, same id).
    /// </summary>
    private int _showGeneration;

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

        // Resx-sourced, read once at construction (same convention every other window/control in
        // this project follows -- see the class doc comment). The row tooltip goes through the
        // resource dictionary rather than a named element, since it has to reach every per-row
        // button instance the DataTemplate stamps out, not just one.
        UngroupAllButton.Content = Strings.FlyoutUngroupAll;
        Resources["Flyout.SplitOneTooltipText"] = Strings.FlyoutSplitOneTooltip;

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

    /// <summary>
    /// Answers "is the pointer on the shelf right now?" for the auto-close below. Supplied by
    /// ShelfWindow, which tests the cursor position against the shelf's placed rectangle -- see
    /// ShelfWindow.IsCursorOverShelf for why <see cref="UIElement.IsMouseOver"/> cannot be used
    /// here. Null (designer/tests) means the flyout only counts its own hover.
    /// </summary>
    public Func<bool>? CursorOverShelf { get; set; }

    /// <summary>A row was clicked: put that one file on the clipboard. Argument is the full path.</summary>
    public event Action<string>? FileActivated;

    /// <summary>A row was dragged into the edge band and refused everywhere else: split it out.
    /// Also raised directly (no drag) by a row's own "split this one off" button -- see
    /// <see cref="OnRowSplitButtonClick"/> -- so both paths funnel through the one event ShelfWindow
    /// already handles.</summary>
    public event Action<string, string>? SplitRequested;

    /// <summary>The header's "ungroup all" button was clicked (v1.3 Task C): expand every path in
    /// the stack currently on show into its own card. Argument is the stack id.</summary>
    public event Action<string>? UngroupAllRequested;

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

        // Reuses CardFilesCountFormat -- the same "ファイル N 件" phrase a stack card's own title
        // already shows -- rather than a second, near-duplicate format string.
        HeaderCountText.Text = string.Format(Strings.CardFilesCountFormat, _paths.Count);

        var rows = _paths.Select(StackFileRow.Create).ToList();
        RowsHost.ItemsSource = rows;
        ScheduleThumbnailResolution(rows);

        // Away from the screen edge, never over it: on a left-edge shelf the flyout opens to the
        // right of the card, and vice versa, so it never lands off-screen.
        var edge = global::TNDrop.App.Settings?.Edge ?? EdgeSide.Left;
        Placement = edge == EdgeSide.Left ? PlacementMode.Right : PlacementMode.Left;
        HorizontalOffset = edge == EdgeSide.Left ? 6 : -6;

        PlacementTarget = placementTarget;

        _hoverMisses = 0;
        IsOpen = true;
    }

    /// <summary>
    /// Kicks off a background <see cref="ShellImagingWorker"/> request per row that needs one
    /// (v1.3 Task C review fix -- see <see cref="_showGeneration"/>'s own remarks for the full
    /// staleness story). Rows are created with <see cref="StackFileRow.Thumbnail"/> still null and
    /// showing the glyph fallback (<see cref="StackFileRow.Icon"/>); each background result, once
    /// it lands, swaps the visual in via <see cref="StackFileRow.ApplyThumbnail"/> and WPF's own
    /// data-binding -- no manual UI refresh needed.
    ///
    /// <para>Requests are fire-and-forget from here: <see cref="StackFileRow.ResolveThumbnail"/>
    /// runs entirely on the worker thread, and only the tiny closure that applies the result
    /// crosses back onto the UI thread via <see cref="Dispatcher.BeginInvoke(Delegate)"/>.</para>
    /// </summary>
    private void ScheduleThumbnailResolution(List<StackFileRow> rows)
    {
        _showGeneration++;
        var generation = _showGeneration;
        var stackId = StackId;

        foreach (var row in rows)
        {
            if (!row.NeedsThumbnail)
            {
                continue;
            }

            var path = row.Path;

            ShellImagingWorker.Enqueue(() =>
            {
                var thumbnail = StackFileRow.ResolveThumbnail(path);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!IsThumbnailResultCurrent(generation, stackId))
                    {
                        // `row` still belongs to whatever list produced it either way -- applying
                        // to an orphaned row that nothing binds to anymore would be harmless, but
                        // skipping it entirely is cheaper and keeps the intent explicit.
                        return;
                    }

                    row.ApplyThumbnail(thumbnail);
                }));
            });
        }
    }

    /// <summary>
    /// Pure staleness decision for a background thumbnail result (v1.3 Task C review fix): true
    /// only when both <paramref name="resultGeneration"/> matches the CURRENT
    /// <see cref="_showGeneration"/> and <paramref name="resultStackId"/> matches the CURRENT
    /// <see cref="StackId"/>. Extracted out of the scheduling closure in
    /// <see cref="ScheduleThumbnailResolution"/> specifically so the rule itself is unit-testable
    /// without depending on real background-thread timing -- a live <see cref="ShellImagingWorker"/>
    /// race is not something a deterministic test can wait on.
    ///
    /// <para>Both conditions are required, not just one: <see cref="StackId"/> alone would miss the
    /// "same stack, closed and reopened" case -- a fresh row list and a fresh generation, but an
    /// unchanged id -- where the OLD rows must still be treated as stale even though the id check
    /// alone would say otherwise.</para>
    /// </summary>
    public bool IsThumbnailResultCurrent(int resultGeneration, string? resultStackId) =>
        resultGeneration == _showGeneration &&
        string.Equals(StackId, resultStackId, StringComparison.Ordinal);

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
    /// <para>The two halves of that question are asked differently, and deliberately so. This
    /// popup's own hover is a plain hit test (<c>Child.IsMouseOver</c>) because hit-testing inside
    /// the captured subtree still works normally. The SHELF's half cannot be: holding SubTree
    /// capture makes WPF report the shelf window as not moused-over even with the pointer sitting
    /// on the card, so it is answered from the cursor position instead
    /// (<see cref="CursorOverShelf"/>). Measured -- with an IsMouseOver term there, a flyout closed
    /// itself under a stationary cursor parked on its own card within ~0.8 s.</para>
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
        var overShelf = CursorOverShelf?.Invoke() ?? false;

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

    /// <summary>Header "ungroup all" button (v1.3 Task C): the explicit-UI primary path onto
    /// <see cref="Core.ItemStore.SplitAll"/>, wired by ShelfWindow.</summary>
    private void OnUngroupAllClick(object sender, RoutedEventArgs e)
    {
        if (StackId is { } stackId)
        {
            UngroupAllRequested?.Invoke(stackId);
        }

        e.Handled = true;
    }

    /// <summary>
    /// Per-row "split this one off" button (v1.3 Task C): the SAME <see cref="SplitRequested"/>
    /// event the edge-band drag raises, just fired directly instead of decided from a drag result --
    /// there is nothing to classify here (no DragDropEffects, no zone check), the click itself IS
    /// the split decision. The row is read from the clicked element's DataContext, since the button
    /// lives inside the per-row DataTemplate and has no other way to know which row it belongs to.
    /// </summary>
    private void OnRowSplitButtonClick(object sender, RoutedEventArgs e)
    {
        if (StackId is { } stackId && sender is FrameworkElement { DataContext: StackFileRow row })
        {
            SplitRequested?.Invoke(stackId, row.Path);
        }

        e.Handled = true;
    }

    /// <summary>True when a hit-test source is inside the per-row action button -- mirrors
    /// ShelfWindow.IsWithinActionButton for the same reason: a click that lands on that button must
    /// be left for its own Click handler, not also read as the row's press/drag gesture.</summary>
    private static bool IsWithinRowActionButton(object? source)
    {
        for (var current = source as DependencyObject; current is not null; current = ParentOf(current))
        {
            if (current is ButtonBase)
            {
                return true;
            }
        }

        return false;
    }

    private void OnRowPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressedRow = null;

        if (IsWithinRowActionButton(e.OriginalSource))
        {
            return;
        }

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
    /// <c>StaysOpen="False"</c> from treating the capture change as an outside click. Measured, not
    /// assumed: the live probe presses a row and then releases it, and the release still finds the
    /// pending press and copies the file -- which it could not do had the popup closed on the press
    /// (<see cref="OnClosed"/> abandons the gesture). Note the contrast with the SHELF taking the
    /// same kind of capture from outside the popup, which does close it -- see the latch in
    /// ShelfWindow.OnCardPreviewMouseLeftButtonDown.</para>
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

        if (row is null || _isDragging || IsWithinRowActionButton(e.OriginalSource))
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
            // No path, no filename -- just the reason half of ShouldSplit failed on, per the
            // project's no-clipboard-content-in-logs rule. See StackGestures.SplitRefusalReason.
            FileLogger.Instance?.Info(Module, StackGestures.SplitRefusalReason(effect, inZone));
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
