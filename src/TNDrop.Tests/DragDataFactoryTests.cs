using System;
using System.IO;
using TNDrop.Core;
using TNDrop.Platform;

public class DragDataFactoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tndrop-test-" + Guid.NewGuid());
    public DragDataFactoryTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [StaFact]
    public void Text_item_yields_unicode_text()
    {
        var d = DragDropSource.BuildDataObject(
            new ClipItem { Kind = ClipKind.Text, Text = "abc" }, _dir);
        Assert.True(d!.GetDataPresent(System.Windows.DataFormats.UnicodeText));
        Assert.Equal("abc", d.GetData(System.Windows.DataFormats.UnicodeText));
    }

    [StaFact]
    public void Files_item_excludes_missing_paths()
    {
        var real = Path.Combine(_dir, "real.txt"); File.WriteAllText(real, "x");
        var item = new ClipItem { Kind = ClipKind.Files, Paths = { real, @"C:\no\such\file.bin" } };
        var d = DragDropSource.BuildDataObject(item, _dir);
        var files = (string[])d!.GetData(System.Windows.DataFormats.FileDrop)!;
        Assert.Equal(new[] { real }, files);
    }

    [StaFact]
    public void Files_item_with_all_missing_yields_null()
    {
        var item = new ClipItem { Kind = ClipKind.Files, Paths = { @"C:\no\a", @"C:\no\b" } };
        Assert.Null(DragDropSource.BuildDataObject(item, _dir));
    }
}
