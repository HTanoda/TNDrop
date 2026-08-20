using System;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using TNDrop.Core;
using TNDrop.Platform;
using TNDrop.Services;

public class CapturePipelineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tndrop-test-" + Guid.NewGuid());
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private CapturePipeline NewPipeline(ItemStore store) =>
        new(store, new ThumbnailService(store.BlobsDir), () => new AppSettings());

    [StaFact]
    public void Text_clip_becomes_text_item()
    {
        var store = new ItemStore(_dir);
        var p = NewPipeline(store);
        Assert.True(p.Process(new CapturedClip { Kind = ClipKind.Text, Text = "hello" }));
        Assert.Equal(ClipKind.Text, store.Items[0].Kind);
    }

    [StaFact]
    public void Duplicate_text_is_not_added_twice()
    {
        var store = new ItemStore(_dir);
        var p = NewPipeline(store);
        p.Process(new CapturedClip { Kind = ClipKind.Text, Text = "same" });
        Assert.False(p.Process(new CapturedClip { Kind = ClipKind.Text, Text = "same" }));
        Assert.Single(store.Items);
    }

    [StaFact]
    public void Eleven_files_become_two_stacks()
    {
        var store = new ItemStore(_dir);
        var p = NewPipeline(store);
        var files = Enumerable.Range(1, 11).Select(i => $@"C:\f\{i}.txt").ToArray();
        Assert.True(p.Process(new CapturedClip { Kind = ClipKind.Files, Files = files }));
        Assert.Equal(2, store.Items.Count);
    }

    [StaFact]
    public void Image_clip_saves_blob_and_thumb()
    {
        var store = new ItemStore(_dir);
        var p = NewPipeline(store);
        var bmp = BitmapSource.Create(4, 4, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null,
                                      new byte[4 * 4 * 4], 16);
        Assert.True(p.Process(new CapturedClip { Kind = ClipKind.Image, Image = bmp }));
        var item = store.Items[0];
        Assert.True(File.Exists(Path.Combine(store.BlobsDir, item.ImageFile!)));
        Assert.True(File.Exists(Path.Combine(store.BlobsDir, item.ThumbFile!)));
    }

    // Regression: ProcessImage used to call ThumbnailService.SaveImage (writing a blob + thumb
    // to disk) BEFORE checking dedup, so a duplicate image capture permanently orphaned two PNG
    // files on every repeat (nothing scans blobs/ for files no ClipItem references). The fix
    // hashes the encoded PNG bytes first and skips the save entirely when it matches the current
    // head, so a duplicate must leave exactly the first capture's two files behind -- not four.
    [StaFact]
    public void Duplicate_image_does_not_orphan_blob_files()
    {
        var store = new ItemStore(_dir);
        var p = NewPipeline(store);
        var bmp = BitmapSource.Create(4, 4, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null,
                                      new byte[4 * 4 * 4], 16);

        Assert.True(p.Process(new CapturedClip { Kind = ClipKind.Image, Image = bmp }));
        Assert.False(p.Process(new CapturedClip { Kind = ClipKind.Image, Image = bmp }));

        Assert.Single(store.Items);
        Assert.Equal(2, Directory.GetFiles(store.BlobsDir).Length);
    }
}
