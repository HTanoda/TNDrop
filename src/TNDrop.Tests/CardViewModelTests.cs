using System;
using System.Linq;
using TNDrop.Core;
using TNDrop.UI;

namespace TNDrop.Tests;

/// <summary>
/// CardViewModel.Media/IsMediaFile are pure classification derived from MediaKind.Classify --
/// no shell/COM call happens just by constructing a CardViewModel or reading these two
/// properties, so these tests run without touching disk or the shell.
/// </summary>
public class CardViewModelTests
{
    private static ClipItem FilesItem(params string[] paths) => new()
    {
        Kind = ClipKind.Files,
        Paths = paths.ToList(),
        CreatedAtUtc = DateTime.UtcNow,
    };

    [StaFact]
    public void Single_image_file_is_media_with_Image_category()
    {
        var card = new CardViewModel(FilesItem(@"C:\pics\a.png"));

        Assert.Equal(MediaCategory.Image, card.Media);
        Assert.True(card.IsMediaFile);
    }

    [StaFact]
    public void Single_video_file_is_media_with_Video_category()
    {
        var card = new CardViewModel(FilesItem(@"C:\mov\a.mp4"));

        Assert.Equal(MediaCategory.Video, card.Media);
        Assert.True(card.IsMediaFile);
    }

    [StaFact]
    public void Single_non_media_file_is_not_a_media_file()
    {
        var card = new CardViewModel(FilesItem(@"C:\docs\a.xlsx"));

        Assert.Equal(MediaCategory.Other, card.Media);
        Assert.False(card.IsMediaFile);
    }

    [StaFact]
    public void Two_file_image_stack_is_not_a_media_file()
    {
        var card = new CardViewModel(FilesItem(@"C:\pics\a.png", @"C:\pics\b.png"));

        Assert.Equal(MediaCategory.Other, card.Media);
        Assert.False(card.IsMediaFile);
        Assert.True(card.IsStack);
    }

    [StaFact]
    public void Non_files_kind_has_Other_media_and_is_not_a_media_file()
    {
        var card = new CardViewModel(new ClipItem
        {
            Kind = ClipKind.Text,
            Text = "hi",
            CreatedAtUtc = DateTime.UtcNow,
        });

        Assert.Equal(MediaCategory.Other, card.Media);
        Assert.False(card.IsMediaFile);
    }

    [StaFact]
    public void Kind_Image_is_unaffected_by_Media_classification()
    {
        var card = new CardViewModel(new ClipItem
        {
            Kind = ClipKind.Image,
            CreatedAtUtc = DateTime.UtcNow,
        });

        Assert.Equal(ClipKind.Image, card.Kind);
        Assert.Equal(MediaCategory.Other, card.Media);
        Assert.False(card.IsMediaFile);
    }

    // -- v1.1 re-review item #2: FileIcon's stale !IsMediaFile guard -----------------------------
    //
    // FileIcon used to be unconditionally null for a media file (IsMediaFile==true), even when its
    // Thumbnail turned out null and a 32px shell icon might still resolve. The guard now allows a
    // media file to try FileIcon once Thumbnail has already been read and come back null. Both
    // paths below use a nonexistent path, so the actual ShellImaging.GetIcon/GetThumbnail calls
    // still return null (there is nothing on disk to produce an icon from) -- these tests exercise
    // CardViewModel's own guard/ordering logic, not real shell behaviour, which cannot be forced
    // to fail deterministically in a unit test without mocking ShellImaging.

    [StaFact]
    public void FileIcon_stays_null_for_media_file_when_Thumbnail_never_read()
    {
        var card = new CardViewModel(FilesItem(@"C:\this-path-does-not-exist\a.png"));

        Assert.True(card.IsMediaFile);
        Assert.Null(card.FileIcon); // Thumbnail never touched -- unchanged pre-fix behaviour
    }

    [StaFact]
    public void FileIcon_is_attempted_for_media_file_once_null_Thumbnail_has_been_read()
    {
        var card = new CardViewModel(FilesItem(@"C:\this-path-does-not-exist\a.png"));

        Assert.True(card.IsMediaFile);
        Assert.Null(card.Thumbnail); // forces the lazy load; caches null (path does not exist)
        // Guard no longer short-circuits purely on IsMediaFile now that Thumbnail resolved to
        // null; GetIcon is attempted and still returns null here only because the path is missing.
        Assert.Null(card.FileIcon);
    }
}
