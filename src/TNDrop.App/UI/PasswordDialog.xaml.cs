using System.Windows;
using TNDrop.Core;
using TNDrop.Resources;

namespace TNDrop.UI;

/// <summary>
/// Small modal for entering an export/import password (v1.6 Task 7). Two shapes, chosen by the
/// private constructor's <c>confirmMode</c> flag via the two static factories below:
/// <see cref="ForExport"/> shows a confirm box + the 8-character hint and enforces
/// <see cref="ExportContainer.MinPasswordLength"/>; <see cref="ForImport"/> shows a single box
/// with no length check at all. That asymmetry is deliberate, not an oversight: a future export
/// policy could raise the minimum length, and an importer that still enforced today's minimum
/// would refuse to even try decrypting an older, shorter-but-correct password. Length validation
/// is exported's responsibility only - the importer either has the right password or it doesn't,
/// and <see cref="TNDrop.Core.ExportContainer.Decrypt"/> is what actually decides that.
/// </summary>
public sealed partial class PasswordDialog : Window
{
    private readonly bool _confirmMode;

    /// <summary>Only meaningful after <c>ShowDialog() == true</c>.</summary>
    public string Password { get; private set; } = "";

    private PasswordDialog(bool confirmMode)
    {
        InitializeComponent();
        _confirmMode = confirmMode;

        LabelText.Text = Strings.PasswordLabel;
        ConfirmLabelText.Text = Strings.PasswordConfirmLabel;
        HintText.Text = Strings.PasswordHint;
        OkButton.Content = Strings.PasswordOk;
        CancelButton.Content = Strings.PasswordCancel;

        if (!confirmMode)
        {
            // Import mode: no confirm box, no length hint (see class doc comment above).
            ConfirmLabelText.Visibility = Visibility.Collapsed;
            Box2.Visibility = Visibility.Collapsed;
            HintText.Visibility = Visibility.Collapsed;
        }

        Loaded += (_, _) => Box1.Focus();
    }

    public static PasswordDialog ForExport() => new(confirmMode: true) { Title = Strings.ExportPasswordTitle };

    public static PasswordDialog ForImport() => new(confirmMode: false) { Title = Strings.ImportPasswordTitle };

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        // Both checks below are confirmMode-only - see the class doc comment on why the import
        // shape skips length validation entirely rather than reusing MinPasswordLength.
        if (_confirmMode && Box1.Password.Length < ExportContainer.MinPasswordLength)
        {
            ShowError(Strings.PasswordTooShort);
            return;
        }

        if (_confirmMode && Box1.Password != Box2.Password)
        {
            ShowError(Strings.PasswordMismatch);
            return;
        }

        Password = Box1.Password;
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
