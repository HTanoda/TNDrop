using TNDrop.Core;
using TNDrop.UI;

/// <summary>
/// v1.2 Task H: the click-to-paste rule. Five independent terms, each of which is a way to fire a
/// Ctrl+V into a window the user did not mean -- so each gets its own test rather than relying on
/// one happy-path case plus a reading of the boolean expression.
/// </summary>
public class ClickPasteTests
{
    /// <summary>The all-clear baseline every negative case below flips exactly one term of.</summary>
    private static bool Eligible(
        ClipKind kind = ClipKind.Text,
        bool setting = true,
        bool ownForeground = false,
        bool focusWithin = false,
        bool modifiers = false) =>
        ClickPaste.ShouldPasteOnClick(kind, setting, ownForeground, focusWithin, modifiers);

    [Theory]
    [InlineData(ClipKind.Text)]
    [InlineData(ClipKind.Link)]
    public void Pastes_for_text_and_link_when_nothing_blocks_it(ClipKind kind)
    {
        Assert.True(Eligible(kind: kind));
    }

    [Theory]
    [InlineData(ClipKind.Files)]
    [InlineData(ClipKind.Image)]
    public void Never_pastes_for_files_or_image_cards(ClipKind kind)
    {
        Assert.False(Eligible(kind: kind));
    }

    [Fact]
    public void Does_not_paste_when_the_setting_is_off()
    {
        Assert.False(Eligible(setting: false));
    }

    [Fact]
    public void Does_not_paste_when_our_own_process_is_in_the_foreground()
    {
        Assert.False(Eligible(ownForeground: true));
    }

    [Fact]
    public void Does_not_paste_while_the_shelf_holds_keyboard_focus()
    {
        Assert.False(Eligible(focusWithin: true));
    }

    [Fact]
    public void Does_not_paste_while_a_physical_modifier_key_is_held()
    {
        Assert.False(Eligible(modifiers: true));
    }

    /// <summary>
    /// The setting is a permission, not an override: turning it on must not resurrect a paste any
    /// of the safety terms has already refused. Guards against a future edit that reorders the
    /// expression into something where the setting short-circuits the rest.
    /// </summary>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void The_setting_being_on_never_overrides_a_safety_term(
        bool ownForeground, bool focusWithin, bool modifiers)
    {
        Assert.False(Eligible(
            setting: true, ownForeground: ownForeground, focusWithin: focusWithin, modifiers: modifiers));
    }

    // ---- Resolve / SuppressReasonCode (Task F, v1.3: the paste-suppressed reason log) --------

    private static ClickPasteResult Resolved(
        ClipKind kind = ClipKind.Text,
        bool setting = true,
        bool ownForeground = false,
        bool focusWithin = false,
        bool modifiers = false) =>
        ClickPaste.Resolve(kind, setting, ownForeground, focusWithin, modifiers);

    [Fact]
    public void Resolve_returns_Paste_exactly_when_ShouldPasteOnClick_would_have_said_true()
    {
        // Pins ShouldPasteOnClick down as a thin wrapper: same five inputs must always agree.
        Assert.Equal(ClickPasteResult.Paste, Resolved());
        Assert.True(Eligible());
    }

    [Theory]
    [InlineData(ClipKind.Files)]
    [InlineData(ClipKind.Image)]
    public void Resolve_returns_NotApplicable_for_files_or_image_cards(ClipKind kind)
    {
        Assert.Equal(ClickPasteResult.NotApplicable, Resolved(kind: kind));
    }

    [Fact]
    public void Resolve_returns_NotApplicable_when_the_setting_is_off()
    {
        Assert.Equal(ClickPasteResult.NotApplicable, Resolved(setting: false));
    }

    [Fact]
    public void Resolve_returns_SelfForeground_when_our_own_process_is_in_the_foreground()
    {
        Assert.Equal(ClickPasteResult.SelfForeground, Resolved(ownForeground: true));
    }

    [Fact]
    public void Resolve_returns_SearchFocus_while_the_shelf_holds_keyboard_focus()
    {
        Assert.Equal(ClickPasteResult.SearchFocus, Resolved(focusWithin: true));
    }

    [Fact]
    public void Resolve_returns_Modifier_while_a_physical_modifier_key_is_held()
    {
        Assert.Equal(ClickPasteResult.Modifier, Resolved(modifiers: true));
    }

    /// <summary>
    /// When more than one safety term would block the click, Resolve reports the first one in
    /// the documented priority order (self-foreground, then search-focus, then modifier) -- not
    /// an arbitrary one of them, so the log line is deterministic.
    /// </summary>
    [Fact]
    public void Resolve_reports_self_foreground_first_when_multiple_terms_would_block()
    {
        Assert.Equal(
            ClickPasteResult.SelfForeground,
            Resolved(ownForeground: true, focusWithin: true, modifiers: true));
    }

    [Fact]
    public void Resolve_reports_search_focus_before_modifier_when_both_would_block()
    {
        Assert.Equal(
            ClickPasteResult.SearchFocus,
            Resolved(ownForeground: false, focusWithin: true, modifiers: true));
    }

    [Theory]
    [InlineData(ClickPasteResult.SelfForeground, "self-foreground")]
    [InlineData(ClickPasteResult.SearchFocus, "search-focus")]
    [InlineData(ClickPasteResult.Modifier, "modifier")]
    public void SuppressReasonCode_returns_the_documented_reason_string(
        ClickPasteResult result, string expected)
    {
        Assert.Equal(expected, ClickPaste.SuppressReasonCode(result));
    }

    [Theory]
    [InlineData(ClickPasteResult.Paste)]
    [InlineData(ClickPasteResult.NotApplicable)]
    public void SuppressReasonCode_returns_null_when_there_is_nothing_to_log(ClickPasteResult result)
    {
        Assert.Null(ClickPaste.SuppressReasonCode(result));
    }
}
