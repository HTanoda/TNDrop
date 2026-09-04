namespace TNDrop.UI;

/// <summary>
/// The single rule for what the shelf's <c>IsKeyboardFocusWithin</c> actually means (v1.8.2).
/// <para>The shelf is WS_EX_NOACTIVATE, so a click never makes it the foreground window -- but
/// Windows still gives the clicked window its own thread's activation and keyboard focus
/// (WM_MOUSEACTIVATE), and WPF then moves keyboard focus onto the clicked element. Because the
/// shelf's thread never becomes the foreground thread, no WM_KILLFOCUS ever arrives when the user
/// goes back to their app, and <c>IsKeyboardFocusWithin</c> stays true for as long as the shelf is
/// visible. Two things read that flag as "the user is in our search box": the click-to-paste
/// guard (<see cref="ClickPaste"/>'s search-focus term) and the retract logic
/// (ShelfWindow.IsPointerInside), and both were wrong every time the flag was stale -- every
/// click re-copied but never pasted, and the shelf never armed its own retract. Measured on the
/// production and development machines (every click logged <c>paste suppressed: search-focus</c>
/// with no search box involved) and reproduced with real SendInput clicks in a throwaway harness.</para>
/// <para>The flag only means "Ctrl+V would land in our search box" while OUR process owns the
/// foreground window, which is exactly the one moment the search box's deliberate activation
/// exception (ShelfWindow.OnSearchBoxPreviewMouseLeftButtonDown) creates. Pure and
/// parameterised, like <see cref="ClickPaste"/>, so both readers resolve the same way and the
/// rule is unit-tested; the Win32 half (InputSender.IsOwnProcessForeground) is read by the caller.</para>
/// </summary>
public static class ShelfFocus
{
    /// <summary>
    /// True when keyboard focus inside the shelf is real: a Ctrl+V right now would type into the
    /// shelf itself. Both terms are required.
    /// </summary>
    public static bool IsLive(bool keyboardFocusWithin, bool ownProcessForeground) =>
        keyboardFocusWithin && ownProcessForeground;

    /// <summary>
    /// True when the shelf reports keyboard focus while some other process owns the foreground:
    /// the leftover of a click on a never-activated window, which nothing else will ever clear.
    /// Callers heal it with <c>Keyboard.ClearFocus()</c>. Together with <see cref="IsLive"/> this
    /// partitions "focus within" exactly, so a click is never both pasted and healed, nor neither.
    /// </summary>
    public static bool IsStale(bool keyboardFocusWithin, bool ownProcessForeground) =>
        keyboardFocusWithin && !ownProcessForeground;
}
