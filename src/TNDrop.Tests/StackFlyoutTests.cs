using System.Collections.Generic;
using System.Windows.Controls;
using TNDrop.Core;
using TNDrop.UI;

/// <summary>
/// v1.3 Task C review fix: the staleness rule that guards a background thumbnail result against
/// landing after the flyout has moved on (closed, or re-shown for a different stack -- including
/// the SAME stack closed and reopened). Extracted into <see cref="StackFlyout.IsThumbnailResultCurrent"/>
/// specifically so it is testable without depending on real <see cref="TNDrop.Services.ShellImagingWorker"/>
/// background-thread timing.
/// </summary>
public class StackFlyoutTests
{
    private static ClipItem Files(string id, params string[] paths) =>
        new() { Id = id, Kind = ClipKind.Files, Paths = new List<string>(paths) };

    [StaFact]
    public void IsThumbnailResultCurrent_true_for_the_generation_and_stack_just_shown()
    {
        var flyout = new StackFlyout();
        var target = new Border();
        var stack = new CardViewModel(Files("a", @"C:\1.txt", @"C:\2.txt"));

        flyout.ShowFor(stack, target);

        Assert.True(flyout.IsThumbnailResultCurrent(1, "a"));
    }

    [StaFact]
    public void IsThumbnailResultCurrent_false_after_a_different_stack_is_shown()
    {
        var flyout = new StackFlyout();
        var target = new Border();
        var stackA = new CardViewModel(Files("a", @"C:\1.txt", @"C:\2.txt"));
        var stackB = new CardViewModel(Files("b", @"C:\3.txt", @"C:\4.txt"));

        flyout.ShowFor(stackA, target);
        flyout.ShowFor(stackB, target);

        // Neither the generation nor the StackId from stack A's request still match.
        Assert.False(flyout.IsThumbnailResultCurrent(1, "a"));
        Assert.True(flyout.IsThumbnailResultCurrent(2, "b"));
    }

    [StaFact]
    public void IsThumbnailResultCurrent_false_when_the_same_stack_is_closed_and_reopened()
    {
        // The case StackId alone would miss: the id is unchanged, but the rows (and the
        // generation) are a fresh set -- a late result tagged with the OLD generation must still
        // be treated as stale even though resultStackId == StackId would say otherwise.
        var flyout = new StackFlyout();
        var target = new Border();
        var stack = new CardViewModel(Files("a", @"C:\1.txt", @"C:\2.txt"));

        flyout.ShowFor(stack, target);
        flyout.IsOpen = false;
        flyout.ShowFor(stack, target);

        Assert.False(flyout.IsThumbnailResultCurrent(1, "a"));
        Assert.True(flyout.IsThumbnailResultCurrent(2, "a"));
    }

    [StaFact]
    public void IsThumbnailResultCurrent_false_for_an_unknown_generation_or_id()
    {
        var flyout = new StackFlyout();
        var target = new Border();
        var stack = new CardViewModel(Files("a", @"C:\1.txt", @"C:\2.txt"));

        flyout.ShowFor(stack, target);

        Assert.False(flyout.IsThumbnailResultCurrent(1, "different-id"));
        Assert.False(flyout.IsThumbnailResultCurrent(99, "a"));
    }
}
