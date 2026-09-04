using TNDrop.UI;

/// <summary>
/// v1.8.2: the "does the shelf really hold keyboard focus" rule behind both the click-to-paste
/// search-focus term and the retract logic's focus term.
/// <para>Background: a click on the WS_EX_NOACTIVATE shelf never makes it the foreground window,
/// but Windows still hands the shelf its own thread's activation and keyboard focus, and WPF then
/// sets IsKeyboardFocusWithin. Because the thread never becomes the foreground thread, no
/// WM_KILLFOCUS ever arrives to clear it -- so IsKeyboardFocusWithin alone is a stale flag whenever
/// another process is in the foreground. It only means "Ctrl+V would land in our search box" while
/// our own process is the foreground.</para>
/// </summary>
public class ShelfFocusTests
{
    [Fact]
    public void Focus_is_live_only_when_within_and_own_process_is_foreground()
    {
        Assert.True(ShelfFocus.IsLive(keyboardFocusWithin: true, ownProcessForeground: true));
    }

    [Fact]
    public void Focus_within_while_another_app_is_foreground_is_not_live()
    {
        Assert.False(ShelfFocus.IsLive(keyboardFocusWithin: true, ownProcessForeground: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void No_focus_within_is_never_live(bool ownProcessForeground)
    {
        Assert.False(ShelfFocus.IsLive(keyboardFocusWithin: false, ownProcessForeground));
    }

    [Fact]
    public void Stale_means_within_but_foreground_belongs_to_someone_else()
    {
        Assert.True(ShelfFocus.IsStale(keyboardFocusWithin: true, ownProcessForeground: false));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Every_other_combination_is_not_stale(bool keyboardFocusWithin, bool ownProcessForeground)
    {
        Assert.False(ShelfFocus.IsStale(keyboardFocusWithin, ownProcessForeground));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Live_and_stale_never_both_hold_and_cover_every_within_case(bool keyboardFocusWithin, bool ownProcessForeground)
    {
        // One resolution: the paste guard reads IsLive, the self-heal reads IsStale. They must
        // partition "focus within" exactly, so a click can never be both pasted and healed, nor
        // neither.
        var live = ShelfFocus.IsLive(keyboardFocusWithin, ownProcessForeground);
        var stale = ShelfFocus.IsStale(keyboardFocusWithin, ownProcessForeground);
        Assert.False(live && stale);
        Assert.Equal(keyboardFocusWithin, live || stale);
    }
}
