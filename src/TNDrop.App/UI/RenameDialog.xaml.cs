using System;
using System.Windows;
using System.Windows.Input;
using TNDrop.Resources;
using MessageBox = System.Windows.MessageBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace TNDrop.UI;

/// <summary>
/// v1.8 のスタック名変更ダイアログ。空で保存 = 名前クリア (自動タイトルに戻る) なので、
/// EditDialog と違い保存ボタンは常に有効。保存の実体は ItemStore.SetName が唯一の入口。
/// 単一インスタンス管理は App 側 (OpenRenameDialog)。
/// </summary>
public sealed partial class RenameDialog : Window
{
    private readonly string _itemId;

    public RenameDialog(string itemId, string? currentName)
    {
        InitializeComponent();
        _itemId = itemId;

        Title = Strings.RenameDialogTitle;
        SaveButton.Content = Strings.EditSaveButton;
        CancelButton.Content = Strings.EditCancelButton;
        HintText.Text = Strings.RenameHint;

        NameTextBox.Text = currentName ?? string.Empty;

        Loaded += (_, _) => { NameTextBox.Focus(); NameTextBox.SelectAll(); };
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var store = global::TNDrop.App.Store;
        if (store is null)
        {
            Close();
            return;
        }

        if (store.SetName(_itemId, NameTextBox.Text))
        {
            store.Save();
            Close();
        }
        else
        {
            // 開いている間に対象カードが消えた (削除・自動削除・リストア)。
            MessageBox.Show(this, Strings.EditItemGone, Strings.RenameDialogTitle,
                MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
    }

    // IsCancel="True" on CancelButton only auto-closes a window opened with ShowDialog() -- this
    // window is opened with Show() (App's single-instance pattern, App.OnRenameDialogRequested,
    // same as EditDialog), so WPF's IsCancel/Esc auto-close never fires here. An explicit Click
    // handler is required for the button, and a Window-level PreviewKeyDown handler is required
    // for Esc (same fix as EditDialog.xaml.cs, v1.7 Task 2 review round 1).
    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            // Mark handled so the closing window's IsCancel button doesn't also see this Esc
            // and call Close() a second time.
            e.Handled = true;
        }
    }
}
