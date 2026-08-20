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
}
