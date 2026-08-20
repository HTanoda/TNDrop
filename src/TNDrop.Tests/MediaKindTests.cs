using TNDrop.Core;

namespace TNDrop.Tests;

/// <summary>
/// MediaKind.Classify は拡張子だけで判定する純粋関数 (I/O なし)。
/// 拡張子リストは v1.1 計画の Global Constraints を正とする。
/// </summary>
public class MediaKindTests
{
    [Theory]
    [InlineData(@"C:\pics\a.png")]
    [InlineData(@"C:\pics\a.jpg")]
    [InlineData(@"C:\pics\a.jpeg")]
    [InlineData(@"C:\pics\a.gif")]
    [InlineData(@"C:\pics\a.bmp")]
    [InlineData(@"C:\pics\a.webp")]
    [InlineData(@"C:\pics\a.ico")]
    [InlineData(@"C:\pics\a.tif")]
    [InlineData(@"C:\pics\a.tiff")]
    [InlineData(@"C:\pics\a.heic")]
    public void Classify_image_extensions_returns_Image(string path)
        => Assert.Equal(MediaCategory.Image, MediaKind.Classify(path));

    [Theory]
    [InlineData(@"C:\mov\a.mp4")]
    [InlineData(@"C:\mov\a.mov")]
    [InlineData(@"C:\mov\a.avi")]
    [InlineData(@"C:\mov\a.wmv")]
    [InlineData(@"C:\mov\a.mkv")]
    [InlineData(@"C:\mov\a.webm")]
    public void Classify_video_extensions_returns_Video(string path)
        => Assert.Equal(MediaCategory.Video, MediaKind.Classify(path));

    [Theory]
    [InlineData(@"C:\docs\book.xlsx")]
    [InlineData(@"C:\docs\book.xls")]
    [InlineData(@"C:\docs\memo.txt")]
    [InlineData(@"C:\docs\slide.pptx")]
    [InlineData(@"C:\docs\report.pdf")]
    [InlineData(@"C:\docs\music.mp3")]      // 音声は動画扱いにしない
    [InlineData(@"C:\docs\archive.zip")]
    [InlineData(@"C:\docs\a.svg")]          // ベクタはシェルサムネに任せるので Other
    public void Classify_non_media_extensions_returns_Other(string path)
        => Assert.Equal(MediaCategory.Other, MediaKind.Classify(path));

    [Theory]
    [InlineData(@"C:\pics\A.PNG", MediaCategory.Image)]
    [InlineData(@"C:\pics\A.JpG", MediaCategory.Image)]
    [InlineData(@"C:\pics\A.HEIC", MediaCategory.Image)]
    [InlineData(@"C:\mov\A.MP4", MediaCategory.Video)]
    [InlineData(@"C:\mov\A.WebM", MediaCategory.Video)]
    [InlineData(@"C:\docs\A.XLSX", MediaCategory.Other)]
    public void Classify_ignores_case(string path, MediaCategory expected)
        => Assert.Equal(expected, MediaKind.Classify(path));

    [Theory]
    [InlineData(@"C:\bin\program")]         // 拡張子なし
    [InlineData(@"C:\bin\.gitignore")]      // ドット始まりは拡張子扱いだが media ではない
    [InlineData("README")]
    public void Classify_without_media_extension_returns_Other(string path)
        => Assert.Equal(MediaCategory.Other, MediaKind.Classify(path));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_null_or_blank_returns_Other(string? path)
        => Assert.Equal(MediaCategory.Other, MediaKind.Classify(path));

    [Theory]
    [InlineData(@"C:\Users\x\Documents")]           // フォルダらしきパス
    [InlineData(@"C:\Users\x\Documents\")]          // 末尾セパレータ付き
    [InlineData(@"C:\my.folder\file")]              // 途中のドットは拡張子ではない
    [InlineData(@"C:\my.folder\")]
    [InlineData(@"\\server\share")]
    public void Classify_directory_like_paths_returns_Other(string path)
        => Assert.Equal(MediaCategory.Other, MediaKind.Classify(path));

    [Fact]
    public void Classify_bare_extension_string_is_accepted()
    {
        // 呼び出し側がファイル名だけを持っているケース。
        Assert.Equal(MediaCategory.Image, MediaKind.Classify("photo.png"));
        Assert.Equal(MediaCategory.Video, MediaKind.Classify("clip.mp4"));
    }

    [Fact]
    public void Classify_does_not_throw_on_invalid_path_characters()
    {
        // Path.GetExtension は無効文字でも例外を投げない (.NET Core 以降) が、
        // 分類器は「どんな文字列でも落ちない」ことを契約とする。
        Assert.Equal(MediaCategory.Image, MediaKind.Classify("a|b?c.png"));
        Assert.Equal(MediaCategory.Other, MediaKind.Classify("\u0000"));
    }
}
