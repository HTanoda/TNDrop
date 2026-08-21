using TNDrop.Core;

namespace TNDrop.UI;

/// <summary>
/// The single rule that decides whether a plain click on a card also pastes into the app the user
/// is working in (v1.2 Task H).
/// <para>Pure and parameterised rather than reading <c>App.Settings</c> and Win32 state itself: the
/// decision has five independent terms and every one of them is a way to fire a keystroke into the
/// wrong window, so it is worth being able to assert all of them in a unit test. The Win32 halves
/// (<see cref="TNDrop.Platform.InputSender.IsOwnProcessForeground"/>,
/// <see cref="TNDrop.Platform.InputSender.AnyModifierDown"/>) are read by the one caller,
/// ShelfWindow.TryPasteOnClick, and passed in.</para>
/// </summary>
public static class ClickPaste
{
    /// <summary>
    /// True when the click that just re-copied <paramref name="kind"/> should also send Ctrl+V.
    /// </summary>
    /// <param name="kind">The clicked card's kind. Only Text and Link paste: a Files or Image
    /// paste lands somewhere the user very likely did not mean it to (a folder view, a document
    /// body) and is far more destructive to undo than a stray line of text, so those two keep the
    /// v1.1 re-copy-only behavior.</param>
    /// <param name="pasteOnClickSetting">AppSettings.PasteOnClick, the user-facing switch.</param>
    /// <param name="ownProcessForeground">True when the foreground window belongs to TNDrop itself.
    /// The shelf is WS_EX_NOACTIVATE precisely so the foreground stays with the user's app and IS
    /// therefore the paste target; if TNDrop is nonetheless in front, there is no target and the
    /// keystroke would go to our own UI.</param>
    /// <param name="keyboardFocusWithin">Window.IsKeyboardFocusWithin. True exactly while the
    /// search box holds its deliberate activation exception (see
    /// ShelfWindow.OnSearchBoxPreviewMouseLeftButtonDown), i.e. while Ctrl+V would paste into the
    /// search box. Checked separately from <paramref name="ownProcessForeground"/> rather than
    /// assumed to imply it: focus and foreground are set by two different calls and can disagree
    /// for a moment either way.</param>
    /// <param name="modifiersDown">True when any physical Ctrl/Shift/Alt/Win key is held. A held
    /// modifier changes what Ctrl+V means in the target app (Ctrl+Shift+V is paste-as-plain-text in
    /// some, Alt+Ctrl+V opens a paste-special dialog in others), so the safe answer is to re-copy
    /// only and let the user paste themselves.</param>
    public static bool ShouldPasteOnClick(
        ClipKind kind,
        bool pasteOnClickSetting,
        bool ownProcessForeground,
        bool keyboardFocusWithin,
        bool modifiersDown) =>
        pasteOnClickSetting
        && (kind == ClipKind.Text || kind == ClipKind.Link)
        && !ownProcessForeground
        && !keyboardFocusWithin
        && !modifiersDown;
}
