using TNDrop.Core;

namespace TNDrop.UI;

/// <summary>
/// The four possible outcomes of <see cref="ClickPaste.Resolve"/>: either the click pastes, the
/// feature does not apply to this click at all (setting off, or a Files/Image card), or exactly
/// one of the three safety terms suppressed it. Kept as one result type rather than a bool plus a
/// separately-computed reason string precisely per the "one resolution" rule this file follows:
/// <see cref="ClickPaste.ShouldPasteOnClick"/> and the Task F (v1.3) suppression-reason log both
/// read off this SAME switch, so the two can never quietly disagree about which term fired.
/// </summary>
public enum ClickPasteResult
{
    /// <summary>Send Ctrl+V.</summary>
    Paste,

    /// <summary>The feature does not apply to this click at all (setting off, or a Files/Image
    /// card) -- not a suppression worth a log line, since nothing was ever going to paste.</summary>
    NotApplicable,

    /// <summary>Suppressed: TNDrop's own window holds the foreground.</summary>
    SelfForeground,

    /// <summary>Suppressed: the shelf's search box holds keyboard focus.</summary>
    SearchFocus,

    /// <summary>Suppressed: a physical Ctrl/Shift/Alt/Win key is held.</summary>
    Modifier
}

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
    /// The one place all five terms are resolved. Checked in the same order the boolean
    /// expression below reads them, so a click blocked by more than one term always reports the
    /// first-listed one -- <paramref name="ownProcessForeground"/> before
    /// <paramref name="keyboardFocusWithin"/> before <paramref name="modifiersDown"/>.
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
    public static ClickPasteResult Resolve(
        ClipKind kind,
        bool pasteOnClickSetting,
        bool ownProcessForeground,
        bool keyboardFocusWithin,
        bool modifiersDown)
    {
        if (!pasteOnClickSetting || (kind != ClipKind.Text && kind != ClipKind.Link))
        {
            return ClickPasteResult.NotApplicable;
        }

        if (ownProcessForeground)
        {
            return ClickPasteResult.SelfForeground;
        }

        if (keyboardFocusWithin)
        {
            return ClickPasteResult.SearchFocus;
        }

        if (modifiersDown)
        {
            return ClickPasteResult.Modifier;
        }

        return ClickPasteResult.Paste;
    }

    /// <summary>
    /// True when the click that just re-copied <paramref name="kind"/> should also send Ctrl+V.
    /// A thin wrapper over <see cref="Resolve"/> -- kept because the five-bool call shape is
    /// already the tested, documented contract every existing call site and test uses; it must
    /// keep returning exactly what it always has, which is exactly what the tests in
    /// ClickPasteTests.cs pin down.
    /// </summary>
    public static bool ShouldPasteOnClick(
        ClipKind kind,
        bool pasteOnClickSetting,
        bool ownProcessForeground,
        bool keyboardFocusWithin,
        bool modifiersDown) =>
        Resolve(kind, pasteOnClickSetting, ownProcessForeground, keyboardFocusWithin, modifiersDown)
            == ClickPasteResult.Paste;

    /// <summary>
    /// The log-line reason code for a suppressed <see cref="ClickPasteResult"/>, or null for
    /// <see cref="ClickPasteResult.Paste"/>/<see cref="ClickPasteResult.NotApplicable"/> (neither
    /// is a suppression worth logging -- one pasted, the other was never going to). No path, no
    /// clipboard content, no filename: a fixed reason code only, per the project's logging rule.
    /// </summary>
    public static string? SuppressReasonCode(ClickPasteResult result) => result switch
    {
        ClickPasteResult.SelfForeground => "self-foreground",
        ClickPasteResult.SearchFocus => "search-focus",
        ClickPasteResult.Modifier => "modifier",
        _ => null
    };
}
