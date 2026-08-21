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

    // -- v1.2 Task A: StackThumbnail ---------------------------------------------------------
    //
    // All three tests use nonexistent paths so the actual ShellImaging.GetThumbnail call returns
    // null safely (no file on disk to produce a preview from) -- these exercise CardViewModel's
    // own gating logic (IsStack + StackFirstMedia), not real shell behaviour.

    [StaFact]
    public void StackThumbnail_is_null_for_non_media_first_path_stack()
    {
        var card = new CardViewModel(FilesItem(@"C:\docs\a.xlsx", @"C:\docs\b.txt"));

        Assert.True(card.IsStack);
        Assert.Equal(MediaCategory.Other, card.StackFirstMedia);
        Assert.Null(card.StackThumbnail);
    }

    [StaFact]
    public void StackThumbnail_is_null_when_first_path_is_missing_on_disk()
    {
        var card = new CardViewModel(FilesItem(
            @"C:\this-path-does-not-exist\a.png", @"C:\this-path-does-not-exist\b.png"));

        Assert.True(card.IsStack);
        Assert.Equal(MediaCategory.Image, card.StackFirstMedia);
        // ShellImaging.GetThumbnail returns null safely for a missing path -- StackThumbnail
        // just passes that through rather than throwing or leaving the flag unresolved.
        Assert.Null(card.StackThumbnail);
        Assert.False(card.HasStackThumbnail);
    }

    [StaFact]
    public void StackThumbnail_is_null_for_a_non_stack_card_even_when_media()
    {
        var card = new CardViewModel(FilesItem(@"C:\pics\a.png"));

        Assert.False(card.IsStack);
        Assert.Equal(MediaCategory.Other, card.StackFirstMedia);
        Assert.Null(card.StackThumbnail);
        Assert.False(card.HasStackThumbnail);
    }

    [StaFact]
    public void StackFirstMedia_is_Video_for_a_video_first_stack()
    {
        var card = new CardViewModel(FilesItem(
            @"C:\this-path-does-not-exist\a.mp4", @"C:\this-path-does-not-exist\b.png"));

        Assert.True(card.IsStack);
        Assert.Equal(MediaCategory.Video, card.StackFirstMedia);
        // Still null in this test only because the path does not exist on disk -- StackFirstMedia
        // (which Cards.xaml's video-badge trigger reads) is correct regardless of that.
        Assert.Null(card.StackThumbnail);
    }
}
