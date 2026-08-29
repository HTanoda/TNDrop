using System;
using System.Windows;
using TNDrop.Resources;
using MessageBox = System.Windows.MessageBox;

namespace TNDrop.UI;

/// <summary>
/// v1.7 のテキスト編集ダイアログ。1 枚のカード (Text/Link) の本文を編集して保存する。
/// 保存の意味論 (本文・種別・ハッシュの同時更新) は ItemStore.UpdateText が唯一の入口で、
/// このクラスは入力と結果表示だけを担当する。単一インスタンス管理は App 側 (OpenEditDialog)。
/// </summary>
public sealed partial class EditDialog : Window
{
    private readonly string _itemId;

    public EditDialog(string itemId, string currentText)
    {
        InitializeComponent();
        _itemId = itemId;

        Title = Strings.EditDialogTitle;
        SaveButton.Content = Strings.EditSaveButton;
        CancelButton.Content = Strings.EditCancelButton;

        BodyTextBox.Text = currentText;
        UpdateSaveEnabled();

        // 開いたらすぐ打てるように。SelectAll はしない (うっかり全置換を防ぐ)。
        Loaded += (_, _) => { BodyTextBox.Focus(); BodyTextBox.CaretIndex = BodyTextBox.Text.Length; };
    }

    private void OnBodyTextChanged(object sender, RoutedEventArgs e) => UpdateSaveEnabled();

    private void UpdateSaveEnabled() =>
        SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(BodyTextBox.Text);

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var store = global::TNDrop.App.Store;
        if (store is null)
        {
            Close();
            return;
        }

        if (store.UpdateText(_itemId, BodyTextBox.Text))
        {
            // 変異メソッドは内部で Save しない作法 (SetPinned と同じ)。編集はこの後に
            // 別の保存点が無いので、ここで即座に永続化する (OnCardActionClick と同じ理由)。
            store.Save();
            Close();
        }
        else
        {
            // 編集中にカードが削除された (手動削除・自動削除・リストア)。設計書 §4。
            MessageBox.Show(this, Strings.EditItemGone, Strings.EditDialogTitle,
                MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
    }
}
