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
}
