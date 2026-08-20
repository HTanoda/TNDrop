using System;
using System.IO;
using System.Linq;
using TNDrop.Core;
using TNDrop.UI;

public class MultiSelectTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tndrop-test-" + Guid.NewGuid());
    private readonly ItemStore _store;
    public MultiSelectTests() { _store = new ItemStore(_dir); }
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
    public void ToggleSelected_flips_state_on_any_card_including_pinned()
    {
        Add(ClipKind.Text, "a");
        Add(ClipKind.Text, "b");
        _store.SetPinned(_store.Items[0].Id, true); // "b" is newest -> Items[0] == "b"

        var vm = new ShelfViewModel(_store);
        Assert.False(vm.SelectionMode);
        Assert.Equal(0, vm.SelectedCount);

        var pinnedId = vm.PinnedCards[0].Id;
        vm.ToggleSelected(pinnedId);
        Assert.True(vm.SelectionMode);
        Assert.Equal(1, vm.SelectedCount);
        Assert.True(vm.PinnedCards[0].Selected);

        var unpinnedId = vm.Cards[0].Id;
        vm.ToggleSelected(unpinnedId);
        Assert.Equal(2, vm.SelectedCount);

        // Toggling again flips back off.
        vm.ToggleSelected(pinnedId);
        Assert.Equal(1, vm.SelectedCount);
        Assert.True(vm.SelectionMode);
    }

    [StaFact]
    public void SelectAllVisible_targets_only_the_current_filter_search_result()
    {
        Add(ClipKind.Text, "会議メモ");
        Add(ClipKind.Link, "https://example.com/page");
        Add(ClipKind.Files, @"C:\docs\a.txt");

        var vm = new ShelfViewModel(_store);
        vm.Filter = CardFilter.Links;

        vm.SelectAllVisible();

        Assert.True(vm.SelectionMode);
        Assert.Equal(1, vm.SelectedCount); // only the Links card, not the Text/Files ones
        Assert.Single(vm.GetSelectedItems());
        Assert.Equal(ClipKind.Link, vm.GetSelectedItems()[0].Kind);
    }

    [StaFact]
    public void SelectAllVisible_does_not_select_pinned_cards()
    {
        Add(ClipKind.Text, "a");
        _store.SetPinned(_store.Items[0].Id, true);
        Add(ClipKind.Text, "b");

        var vm = new ShelfViewModel(_store);
        vm.SelectAllVisible();

        Assert.Equal(1, vm.SelectedCount); // "b" only; "a" is pinned and not in Cards
        Assert.False(vm.PinnedCards[0].Selected);
    }

    [StaFact]
    public void ClearSelection_clears_everything_including_pinned()
    {
        Add(ClipKind.Text, "a");
        Add(ClipKind.Text, "b");
        _store.SetPinned(_store.Items[0].Id, true);

        var vm = new ShelfViewModel(_store);
        vm.ToggleSelected(vm.PinnedCards[0].Id);
        vm.ToggleSelected(vm.Cards[0].Id);
        Assert.Equal(2, vm.SelectedCount);

        vm.ClearSelection();

        Assert.False(vm.SelectionMode);
        Assert.Equal(0, vm.SelectedCount);
        Assert.Empty(vm.GetSelectedItems());
    }

    [StaFact]
    public void GetSelectedItems_returns_the_underlying_ClipItems()
    {
        Add(ClipKind.Text, "hello");
        Add(ClipKind.Text, "world");

        var vm = new ShelfViewModel(_store);
        vm.ToggleSelected(vm.Cards[1].Id); // "hello", the older/second one

        var selected = vm.GetSelectedItems();
        Assert.Single(selected);
        Assert.Equal("hello", selected[0].Text);
    }

    [StaFact]
    public void RemoveSelected_removes_from_store_and_exits_selection_mode()
    {
        Add(ClipKind.Text, "keep");
        Add(ClipKind.Text, "drop-me");

        var vm = new ShelfViewModel(_store);
        var dropId = vm.Cards[0].Id; // "drop-me" is newest
        vm.ToggleSelected(dropId);
        Assert.True(vm.SelectionMode);

        vm.RemoveSelected();

        Assert.False(vm.SelectionMode);
        Assert.Equal(0, vm.SelectedCount);
        Assert.Single(vm.Cards);
        Assert.Equal("keep", vm.Cards[0].Item.Text);
        Assert.DoesNotContain(_store.Items, i => i.Id == dropId);
    }

    [StaFact]
    public void RemoveSelected_with_nothing_selected_is_a_no_op()
    {
        Add(ClipKind.Text, "a");
        var vm = new ShelfViewModel(_store);

        vm.RemoveSelected();

        Assert.Single(vm.Cards);
        Assert.Single(_store.Items);
    }
}
