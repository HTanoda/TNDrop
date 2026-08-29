using System;
using System.Windows;
using System.Windows.Input;
using TNDrop.Resources;
using MessageBox = System.Windows.MessageBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

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

    // IsCancel="True" on CancelButton only auto-closes a window opened with ShowDialog() -- this
    // window is opened with Show() (App's single-instance pattern, App.OnEditDialogRequested), so
    // WPF's IsCancel/Esc auto-close never fires here. An explicit Click handler is required for
    // the button, and a Window-level PreviewKeyDown handler is required for Esc (review round 1
    // fix, v1.7 Task 2). Kept IsCancel="True" in the XAML anyway -- harmless, and it still
    // documents intent for anyone reading the markup.
    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    // AcceptsReturn="True" makes BodyTextBox swallow Enter (needed for multi-line editing), but it
    // does not swallow Esc, so this handler at the Window level still sees it regardless of which
    // control has focus.
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
