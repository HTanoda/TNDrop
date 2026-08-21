using TNDrop.Platform;

namespace TNDrop.Tests;

public class ClipboardIoTests
{
    [Fact]
    public void HasPrivacyFlag_detects_known_formats()
    {
        Assert.True(ClipboardIo.HasPrivacyFlag(new[] { "Text", "Clipboard Viewer Ignore" }));
        Assert.True(ClipboardIo.HasPrivacyFlag(new[] { "ExcludeClipboardContentFromMonitorProcessing" }));
        Assert.False(ClipboardIo.HasPrivacyFlag(new[] { "Text", "HTML Format" }));
    }

    // Legacy overload has no way to read the DWORD payload, so it must keep the OLD
    // presence-based behavior for CanIncludeInClipboardHistory too (documented fallback).
    [Fact]
    public void HasPrivacyFlag_falls_back_to_presence_for_history_format_with_no_reader()
    {
        Assert.True(ClipboardIo.HasPrivacyFlag(new[] { "CanIncludeInClipboardHistory" }));
    }

    [Fact]
    public void EvaluatePrivacy_null_formats_is_not_excluded()
    {
        var result = ClipboardIo.EvaluatePrivacy(null, () => 1);
        Assert.False(result.Excluded);
        Assert.Null(result.MatchedFormat);
    }

    [Fact]
    public void EvaluatePrivacy_no_privacy_format_is_not_excluded()
    {
        var result = ClipboardIo.EvaluatePrivacy(new[] { "Text", "HTML Format" }, () => 0);
        Assert.False(result.Excluded);
    }

    [Theory]
    [InlineData("ExcludeClipboardContentFromMonitorProcessing")]
    [InlineData("Clipboard Viewer Ignore")]
    public void EvaluatePrivacy_excludes_presence_based_formats_regardless_of_reader(string format)
    {
        // The reader would say "don't exclude" (nonzero) if it were ever consulted -- proving the
        // presence-based formats short-circuit before the value reader runs at all.
        var readerCalled = false;
        var result = ClipboardIo.EvaluatePrivacy(new[] { format }, () => { readerCalled = true; return 1; });

        Assert.True(result.Excluded);
        Assert.Equal(format, result.MatchedFormat);
        Assert.False(readerCalled);
    }

    [Fact]
    public void EvaluatePrivacy_history_format_value_zero_excludes()
    {
        var result = ClipboardIo.EvaluatePrivacy(new[] { "CanIncludeInClipboardHistory" }, () => 0);
        Assert.True(result.Excluded);
        Assert.Equal("CanIncludeInClipboardHistory", result.MatchedFormat);
    }

    [Fact]
    public void EvaluatePrivacy_history_format_value_one_does_not_exclude()
    {
        var result = ClipboardIo.EvaluatePrivacy(new[] { "CanIncludeInClipboardHistory" }, () => 1);
        Assert.False(result.Excluded);
        Assert.Null(result.MatchedFormat);
    }

    [Fact]
    public void EvaluatePrivacy_history_format_unreadable_value_does_not_exclude()
    {
        // null == "could not read the payload" -- per the brief, an unreadable value must NOT
        // exclude (screenshots must not be silently dropped because a read failed).
        var result = ClipboardIo.EvaluatePrivacy(new[] { "CanIncludeInClipboardHistory" }, () => null);
        Assert.False(result.Excluded);
    }

    [Fact]
    public void EvaluatePrivacy_history_format_reader_throwing_does_not_exclude()
    {
        var result = ClipboardIo.EvaluatePrivacy(
            new[] { "CanIncludeInClipboardHistory" },
            () => throw new InvalidOperationException("simulated GlobalLock failure"));

        Assert.False(result.Excluded);
    }

    [Fact]
    public void EvaluatePrivacy_history_format_absent_never_invokes_reader()
    {
        var readerCalled = false;
        var result = ClipboardIo.EvaluatePrivacy(new[] { "Text" }, () => { readerCalled = true; return 0; });

        Assert.False(result.Excluded);
        Assert.False(readerCalled);
    }

    [Fact]
    public void EvaluatePrivacy_history_format_with_null_reader_falls_back_to_presence()
    {
        var result = ClipboardIo.EvaluatePrivacy(new[] { "CanIncludeInClipboardHistory" }, null);
        Assert.True(result.Excluded);
        Assert.Equal("CanIncludeInClipboardHistory", result.MatchedFormat);
    }

    [Fact]
    public void EvaluatePrivacy_exclude_format_wins_over_history_format_value_one()
    {
        var readerCalled = false;
        var result = ClipboardIo.EvaluatePrivacy(
            new[] { "CanIncludeInClipboardHistory", "ExcludeClipboardContentFromMonitorProcessing" },
            () => { readerCalled = true; return 1; });

        Assert.True(result.Excluded);
        Assert.Equal("ExcludeClipboardContentFromMonitorProcessing", result.MatchedFormat);
        Assert.False(readerCalled);
    }
}
