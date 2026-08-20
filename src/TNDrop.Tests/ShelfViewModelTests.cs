using System;
using System.IO;
using TNDrop.Core;
using TNDrop.UI;

public class ShelfViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tndrop-test-" + Guid.NewGuid());
    private readonly ItemStore _store;
    public ShelfViewModelTests() { _store = new ItemStore(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private void Add(ClipKind kind, string content)
    {
        var item = kind == ClipKind.Files
            ? ItemStore.BuildFileItems(new[] { content }, DateTime.UtcNow)[0]
            : new ClipItem { Kind = kind, Text = content, CreatedAtUtc = DateTime.UtcNow,
                             ContentHash = ItemStore.Fnv1a(System.Text.Encoding.UTF8.GetBytes(content)) };
        _store.TryAdd(item);
    }

    [StaFact]
    public void Filter_and_search_narrow_cards()
    {
        Add(ClipKind.Text, "会議メモ");
        Add(ClipKind.Link, "https://example.com/page");
        Add(ClipKind.Files, @"C:\docs\申請書.xlsx");
        var vm = new ShelfViewModel(_store);
        Assert.Equal(3, vm.Cards.Count);
        vm.Filter = CardFilter.Links;
        Assert.Single(vm.Cards);
        vm.Filter = CardFilter.All;
        vm.SearchText = "申請";
        Assert.Single(vm.Cards);
        Assert.Equal(ClipKind.Files, vm.Cards[0].Kind);
    }

    [StaFact]
    public void Pinned_items_go_to_pinned_deck()
    {
        Add(ClipKind.Text, "a");
        _store.SetPinned(_store.Items[0].Id, true);
        var vm = new ShelfViewModel(_store);
        Assert.Empty(vm.Cards);
        Assert.Single(vm.PinnedCards);
    }

    // -- v1.1 Task B: unified image classification -----------------------------------------
    //
    // A lone image-extension file counts as 画像 (Images), not ファイル (Files); a lone video
    // stays ファイル; a stack (2+ paths) always stays ファイル even if every path is an image.
    // MatchesFilter and the Count* fields must agree on all three, since they are computed from
    // the same helper (one-resolution rule) -- see ShelfViewModel.IsSingleImageFile.

    [StaFact]
    public void Single_image_file_counts_as_Images_not_Files()
    {
        Add(ClipKind.Files, @"C:\pics\photo.png");
        var vm = new ShelfViewModel(_store);

        Assert.Equal(1, vm.CountImages);
        Assert.Equal(0, vm.CountFiles);

        vm.Filter = CardFilter.Images;
        Assert.Single(vm.Cards);

        vm.Filter = CardFilter.Files;
        Assert.Empty(vm.Cards);
    }

    [StaFact]
    public void Single_video_file_counts_as_Files_not_Images()
    {
        Add(ClipKind.Files, @"C:\mov\clip.mp4");
        var vm = new ShelfViewModel(_store);

        Assert.Equal(0, vm.CountImages);
        Assert.Equal(1, vm.CountFiles);

        vm.Filter = CardFilter.Files;
        Assert.Single(vm.Cards);

        vm.Filter = CardFilter.Images;
        Assert.Empty(vm.Cards);
    }

    [StaFact]
    public void Two_file_image_stack_counts_as_Files_not_Images()
    {
        var paths = new[] { @"C:\pics\a.png", @"C:\pics\b.png" };
        _store.TryAdd(ItemStore.BuildFileItems(paths, DateTime.UtcNow)[0]);
        var vm = new ShelfViewModel(_store);

        Assert.Equal(0, vm.CountImages);
        Assert.Equal(1, vm.CountFiles);

        vm.Filter = CardFilter.Files;
        Assert.Single(vm.Cards);

        vm.Filter = CardFilter.Images;
        Assert.Empty(vm.Cards);
    }

    [StaFact]
    public void Kind_Image_blob_still_counts_as_Images_unchanged()
    {
        var item = new ClipItem
        {
            Kind = ClipKind.Image,
            CreatedAtUtc = DateTime.UtcNow,
            ContentHash = 1,
        };
        _store.TryAdd(item);
        var vm = new ShelfViewModel(_store);

        Assert.Equal(1, vm.CountImages);
        Assert.Equal(0, vm.CountFiles);
    }
}
