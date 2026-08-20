using System;
using System.Collections.Generic;
using System.IO;
using TNDrop.Core;
using TNDrop.Platform;
using TNDrop.UI;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;

/// <summary>
/// The pure decisions behind Task 14's stack gestures: the split edge-zone hit test, the
/// merge-acceptability predicate, the single-row drag payload, and the size formatting the flyout
/// rows render.
/// </summary>
public class StackGesturesTests
{
    // 1920x1040 work area at the origin, the same shape ShelfPlacementTests uses.
    private static readonly ShelfPlacement.Rect Work = new(0, 0, 1920, 1040);

    // A work area that does NOT start at 0,0: catches a hit test that hard-codes the screen origin
    // instead of using the work area it was handed.
    private static readonly ShelfPlacement.Rect SecondMonitor = new(1920, 100, 1280, 900);

    // ---- split edge zone -------------------------------------------------------------------

    [Theory]
    [InlineData(0, true)]        // exactly on the edge
    [InlineData(1, true)]        // over the shelf, hard against the edge
    [InlineData(59, true)]       // just inside the band
    [InlineData(60, true)]       // the band boundary itself counts
    [InlineData(61, false)]      // over the shelf, but past the band
    [InlineData(300, false)]     // still over the shelf (340 wide) -- an ordinary drop
    [InlineData(-30, true)]      // overshot off-screen past the edge
    [InlineData(-90, false)]     // far enough out to be another monitor, not a flick at this edge
    public void SplitZone_left_edge(double x, bool expected) =>
        Assert.Equal(expected, StackGestures.IsInSplitZone(Work, EdgeSide.Left, x, 500));

    [Theory]
    [InlineData(1920, true)]
    [InlineData(1919, true)]
    [InlineData(1861, true)]
    [InlineData(1859, false)]
    [InlineData(1950, true)]     // overshot past the right edge
    [InlineData(2100, false)]
    public void SplitZone_right_edge(double x, bool expected) =>
        Assert.Equal(expected, StackGestures.IsInSplitZone(Work, EdgeSide.Right, x, 500));

    [Fact]
    public void SplitZone_rejects_points_above_or_below_the_work_area()
    {
        // Right x, wrong monitor: a release on a screen stacked above/below must not split.
        Assert.False(StackGestures.IsInSplitZone(Work, EdgeSide.Left, 10, -400));
        Assert.False(StackGestures.IsInSplitZone(Work, EdgeSide.Left, 10, 1600));

        // ...but a small overshoot past the top/bottom (the taskbar strip) still counts.
        Assert.True(StackGestures.IsInSplitZone(Work, EdgeSide.Left, 10, -40));
        Assert.True(StackGestures.IsInSplitZone(Work, EdgeSide.Left, 10, 1080));
    }

    [Fact]
    public void SplitZone_is_relative_to_the_given_work_area_not_the_screen_origin()
    {
        // x=10 is the LEFT edge band of the primary monitor but nowhere near this one's.
        Assert.False(StackGestures.IsInSplitZone(SecondMonitor, EdgeSide.Left, 10, 500));
        Assert.True(StackGestures.IsInSplitZone(SecondMonitor, EdgeSide.Left, 1930, 500));
        Assert.True(StackGestures.IsInSplitZone(SecondMonitor, EdgeSide.Right, 3195, 500));
    }

    [Fact]
    public void SplitZone_band_width_is_configurable()
    {
        Assert.False(StackGestures.IsInSplitZone(Work, EdgeSide.Left, 30, 500, bandDip: 10));
        Assert.True(StackGestures.IsInSplitZone(Work, EdgeSide.Left, 8, 500, bandDip: 10));
    }

    // ---- ShouldSplit -----------------------------------------------------------------------

    [Theory]
    [InlineData(DragDropEffects.None, true, true)]
    [InlineData(DragDropEffects.None, false, false)]   // dropped nowhere, but not at the edge
    [InlineData(DragDropEffects.Copy, true, false)]    // an app took it: an ordinary file drop
    [InlineData(DragDropEffects.Link, true, false)]
    [InlineData(DragDropEffects.Copy, false, false)]
    public void ShouldSplit_needs_both_a_refused_drop_and_the_edge_zone(
        DragDropEffects effect, bool inZone, bool expected) =>
        Assert.Equal(expected, StackGestures.ShouldSplit(effect, inZone));

    // ---- merge acceptability ---------------------------------------------------------------

    private static ClipItem Files(string id, params string[] paths) =>
        new() { Id = id, Kind = ClipKind.Files, Paths = new List<string>(paths) };

    private static ClipItem Of(string id, ClipKind kind) => new() { Id = id, Kind = kind };

    [Fact]
    public void CanAcceptMerge_files_onto_files()
    {
        Assert.True(StackGestures.CanAcceptMerge(Files("a", @"C:\1.txt"), Files("b", @"C:\2.txt")));
    }

    [Fact]
    public void CanAcceptMerge_refuses_a_card_onto_itself()
    {
        var card = Files("a", @"C:\1.txt");
        Assert.False(StackGestures.CanAcceptMerge(card, card));
        // Same id, different instances (the store hands out snapshots) must be refused too.
        Assert.False(StackGestures.CanAcceptMerge(Files("a", @"C:\1.txt"), Files("a", @"C:\1.txt")));
    }

    [Theory]
    [InlineData(ClipKind.Text)]
    [InlineData(ClipKind.Link)]
    [InlineData(ClipKind.Image)]
    public void CanAcceptMerge_refuses_every_non_files_combination(ClipKind other)
    {
        Assert.False(StackGestures.CanAcceptMerge(Files("a", @"C:\1.txt"), Of("b", other)));
        Assert.False(StackGestures.CanAcceptMerge(Of("a", other), Files("b", @"C:\1.txt")));
        Assert.False(StackGestures.CanAcceptMerge(Of("a", other), Of("b", other)));
    }

    [Fact]
    public void CanAcceptMerge_refuses_nulls()
    {
        Assert.False(StackGestures.CanAcceptMerge(null, Files("b", @"C:\1.txt")));
        Assert.False(StackGestures.CanAcceptMerge(Files("a", @"C:\1.txt"), null));
        Assert.False(StackGestures.CanAcceptMerge(null, null));
    }

    [Fact]
    public void CanAcceptMerge_does_not_pre_judge_the_ten_file_limit()
    {
        // Nine plus two would be refused by ItemStore.TryMergeFiles -- but the drop must still be
        // offered so the user gets the shake + "up to 10" message instead of a silent no-drop.
        var nine = Files("a", @"C:\1", @"C:\2", @"C:\3", @"C:\4", @"C:\5", @"C:\6", @"C:\7", @"C:\8", @"C:\9");
        var two = Files("b", @"C:\10", @"C:\11");
        Assert.True(StackGestures.CanAcceptMerge(nine, two));
    }

    // ---- stack-row drag payload ------------------------------------------------------------

    [Fact]
    public void EncodeStackPath_round_trips()
    {
        var encoded = DragDropSource.EncodeStackPath("abc123", @"C:\dir\a b.txt");
        Assert.True(DragDropSource.TryDecodeStackPath(encoded, out var id, out var path));
        Assert.Equal("abc123", id);
        Assert.Equal(@"C:\dir\a b.txt", path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-separator")]
    [InlineData("\nleading")]      // empty stack id
    [InlineData("trailing\n")]     // empty path
    public void TryDecodeStackPath_refuses_malformed_markers(string? encoded)
    {
        Assert.False(DragDropSource.TryDecodeStackPath(encoded, out var id, out var path));
        Assert.Equal(string.Empty, id);
        Assert.Equal(string.Empty, path);
    }

    [StaFact]
    public void BuildStackRowDataObject_carries_one_file_plus_both_markers()
    {
        using var temp = new TempDir();
        var a = temp.WriteFile("a.txt", "a");
        var b = temp.WriteFile("b.txt", "bb");
        var stack = Files("stack-1", a, b);

        var data = DragDropSource.BuildStackRowDataObject(stack, b);

        Assert.NotNull(data);
        var files = data!.GetData(DataFormats.FileDrop) as string[];
        Assert.Equal(new[] { b }, files);
        Assert.Equal("stack-1", data.GetData(DragDropSource.CardIdFormat));

        var marker = data.GetData(DragDropSource.StackPathFormat) as string;
        Assert.True(DragDropSource.TryDecodeStackPath(marker, out var stackId, out var path));
        Assert.Equal("stack-1", stackId);
        Assert.Equal(b, path);
    }

    [StaFact]
    public void BuildStackRowDataObject_refuses_a_path_the_stack_no_longer_holds()
    {
        using var temp = new TempDir();
        var a = temp.WriteFile("a.txt", "a");
        var orphan = temp.WriteFile("orphan.txt", "o");

        Assert.Null(DragDropSource.BuildStackRowDataObject(Files("s", a), orphan));
    }

    [StaFact]
    public void BuildStackRowDataObject_refuses_a_missing_file_and_a_non_files_card()
    {
        using var temp = new TempDir();
        var gone = Path.Combine(temp.Path, "never-written.txt");

        Assert.Null(DragDropSource.BuildStackRowDataObject(Files("s", gone), gone));

        var text = Of("t", ClipKind.Text);
        text.Paths.Add(@"C:\1.txt");
        Assert.Null(DragDropSource.BuildStackRowDataObject(text, @"C:\1.txt"));

        Assert.Null(DragDropSource.BuildStackRowDataObject(null, @"C:\1.txt"));
        Assert.Null(DragDropSource.BuildStackRowDataObject(Files("s", @"C:\1.txt"), null));
    }

    [StaFact]
    public void A_row_drag_is_a_self_drag_but_never_a_merge_drag()
    {
        using var temp = new TempDir();
        var a = temp.WriteFile("a.txt", "a");
        var row = DragDropSource.BuildStackRowDataObject(Files("stack-1", a), a)!;

        // It looks like a self-drag (so the shelf's own drag-IN still ignores it: no re-add)...
        Assert.True(DragDropTarget.IsSelfDrag(row));
        Assert.False(DragDropTarget.HasAcceptablePayload(row));
        Assert.Null(DragDropTarget.ClipFromDataObject(row));

        // ...but it must never be treated as a whole-card merge.
        Assert.True(DragDropTarget.IsStackRowDrag(row));
        Assert.False(DragDropTarget.IsCardMergeDrag(row));
        Assert.Equal(("stack-1", a), DragDropTarget.StackRowOf(row));
    }

    [StaFact]
    public void A_whole_card_drag_is_a_merge_drag_and_carries_no_stack_row()
    {
        using var temp = new TempDir();
        var a = temp.WriteFile("a.txt", "a");
        var card = DragDropSource.BuildDataObject(Files("card-9", a), temp.Path)!;

        Assert.True(DragDropTarget.IsCardMergeDrag(card));
        Assert.False(DragDropTarget.IsStackRowDrag(card));
        Assert.Equal("card-9", DragDropTarget.SourceCardId(card));
        Assert.Null(DragDropTarget.StackRowOf(card));
    }

    [StaFact]
    public void An_external_payload_is_neither_a_merge_nor_a_row_drag()
    {
        var external = new DataObject();
        external.SetData(DataFormats.FileDrop, new[] { @"C:\somewhere\else.txt" });

        Assert.False(DragDropTarget.IsCardMergeDrag(external));
        Assert.False(DragDropTarget.IsStackRowDrag(external));
        Assert.Null(DragDropTarget.SourceCardId(external));
    }

    // ---- row rendering ---------------------------------------------------------------------

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1L, "1 B")]
    [InlineData(512L, "512 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1048576L, "1 MB")]
    [InlineData(1572864L, "1.5 MB")]
    [InlineData(1073741824L, "1 GB")]
    [InlineData(5368709120L, "5 GB")]
    public void FormatSize(long bytes, string expected) =>
        Assert.Equal(expected, StackFileRow.FormatSize(bytes));

    [Fact]
    public void Row_reflects_a_real_file_a_directory_and_a_missing_path()
    {
        using var temp = new TempDir();
        var file = temp.WriteFile("data.bin", new string('x', 2048));
        var dir = Directory.CreateDirectory(Path.Combine(temp.Path, "sub")).FullName;
        var gone = Path.Combine(temp.Path, "gone.txt");

        var fileRow = StackFileRow.Create(file);
        Assert.True(fileRow.Exists);
        Assert.Equal("data.bin", fileRow.FileName);
        Assert.Equal("2 KB", fileRow.SizeText);

        var dirRow = StackFileRow.Create(dir);
        Assert.True(dirRow.Exists);
        Assert.Equal("sub", dirRow.FileName);
        Assert.Equal(string.Empty, dirRow.SizeText);          // a folder has no meaningful size
        Assert.NotEqual(fileRow.Icon, dirRow.Icon);

        var goneRow = StackFileRow.Create(gone);
        Assert.False(goneRow.Exists);
        Assert.Equal("gone.txt", goneRow.FileName);           // still nameable, just greyed out
        Assert.NotEqual(string.Empty, goneRow.SizeText);      // shows the localized FileMissing text
        Assert.NotEqual(fileRow.Icon, goneRow.Icon);
    }

    [Fact]
    public void Row_falls_back_to_the_whole_path_when_there_is_no_leaf_name()
    {
        var row = StackFileRow.Create(@"D:\");
        Assert.Equal(@"D:\", row.FileName);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "tndrop-stack-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string WriteFile(string name, string content)
        {
            var full = System.IO.Path.Combine(Path, name);
            File.WriteAllText(full, content);
            return full;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Leftover temp files are not worth failing a test over.
            }
        }
    }
}
