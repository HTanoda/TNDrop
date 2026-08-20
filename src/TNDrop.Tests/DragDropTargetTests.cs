using System.Windows.Media;
using System.Windows.Media.Imaging;
using TNDrop.Core;
using TNDrop.Platform;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;

public class DragDropTargetTests
{
    private static BitmapSource TinyBitmap() =>
        BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgr32, null, new byte[2 * 2 * 4], 8);

    [StaFact]
    public void FileDrop_becomes_files_clip()
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { @"C:\a.txt", @"C:\b.txt" });

        Assert.True(DragDropTarget.HasAcceptablePayload(data));
        var clip = DragDropTarget.ClipFromDataObject(data);
        Assert.NotNull(clip);
        Assert.Equal(ClipKind.Files, clip!.Kind);
        Assert.Equal(new[] { @"C:\a.txt", @"C:\b.txt" }, clip.Files);
    }

    [StaFact]
    public void Url_text_becomes_link_clip()
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, "https://example.com/page");

        var clip = DragDropTarget.ClipFromDataObject(data);
        Assert.NotNull(clip);
        Assert.Equal(ClipKind.Link, clip!.Kind);
        Assert.Equal("https://example.com/page", clip.Text);
    }

    [StaFact]
    public void Plain_text_becomes_text_clip()
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, "just some words");

        var clip = DragDropTarget.ClipFromDataObject(data);
        Assert.NotNull(clip);
        Assert.Equal(ClipKind.Text, clip!.Kind);
    }

    [StaFact]
    public void Bitmap_becomes_image_clip()
    {
        var data = new DataObject();
        data.SetImage(TinyBitmap());

        var clip = DragDropTarget.ClipFromDataObject(data);
        Assert.NotNull(clip);
        Assert.Equal(ClipKind.Image, clip!.Kind);
        Assert.NotNull(clip.Image);
    }

    [StaFact]
    public void FileDrop_takes_priority_over_unicode_text()
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { @"C:\a.txt" });
        data.SetData(DataFormats.UnicodeText, "https://example.com");

        var clip = DragDropTarget.ClipFromDataObject(data);
        Assert.Equal(ClipKind.Files, clip!.Kind);
    }

    [StaFact]
    public void Bitmap_takes_priority_over_unicode_text()
    {
        var data = new DataObject();
        data.SetImage(TinyBitmap());
        data.SetData(DataFormats.UnicodeText, "hello");

        var clip = DragDropTarget.ClipFromDataObject(data);
        Assert.Equal(ClipKind.Image, clip!.Kind);
    }

    [StaFact]
    public void Self_drag_marker_is_ignored_entirely()
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, "hello");
        data.SetData(DragDropSource.CardIdFormat, "some-card-id");

        Assert.True(DragDropTarget.IsSelfDrag(data));
        Assert.False(DragDropTarget.HasAcceptablePayload(data));
        Assert.Null(DragDropTarget.ClipFromDataObject(data));
    }

    [StaFact]
    public void Whitespace_only_text_yields_no_clip_despite_acceptable_format()
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, "   ");

        Assert.True(DragDropTarget.HasAcceptablePayload(data));
        Assert.Null(DragDropTarget.ClipFromDataObject(data));
    }

    [StaFact]
    public void Unrecognised_payload_is_not_acceptable()
    {
        var data = new DataObject();
        data.SetData("SomeCustomFormat", 42);

        Assert.False(DragDropTarget.HasAcceptablePayload(data));
        Assert.Null(DragDropTarget.ClipFromDataObject(data));
    }
}
