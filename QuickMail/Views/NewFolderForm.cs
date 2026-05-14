using System.Windows.Forms;

namespace QuickMail.Views;

/// <summary>
/// Simple dialog that collects a new folder name from the user.
/// </summary>
public sealed class NewFolderForm : Form
{
    private readonly Label _parentLabel;
    private readonly TextBox _nameBox;
    private readonly Button _okButton;
    private readonly Button _cancelButton;

    public string ParentFolderName
    {
        set => _parentLabel.Text = string.IsNullOrEmpty(value)
            ? string.Empty
            : $"Parent folder: {value}";
    }

    public string FolderName => _nameBox.Text.Trim();

    public NewFolderForm()
    {
        Text            = "New Folder";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        ShowInTaskbar   = false;
        ClientSize      = new System.Drawing.Size(320, 130);
        StartPosition   = FormStartPosition.CenterParent;

        _parentLabel = new Label
        {
            Left   = 12,
            Top    = 14,
            Width  = 296,
            Height = 20,
            AutoSize = false,
        };

        var nameLabel = new Label
        {
            Text   = "&Folder name:",
            Left   = 12,
            Top    = 42,
            Width  = 296,
            Height = 18,
            AutoSize = false,
        };

        _nameBox = new TextBox
        {
            Left    = 12,
            Top     = 62,
            Width   = 296,
            TabIndex = 0,
            AccessibleName = "Folder name",
        };

        _okButton = new Button
        {
            Text     = "&OK",
            Left     = 148,
            Top      = 94,
            Width    = 75,
            Height   = 26,
            TabIndex = 1,
        };

        _cancelButton = new Button
        {
            Text     = "Cancel",
            Left     = 232,
            Top      = 94,
            Width    = 75,
            Height   = 26,
            TabIndex = 2,
            DialogResult = DialogResult.Cancel,
        };

        AcceptButton = _okButton;
        CancelButton = _cancelButton;

        _okButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_nameBox.Text)) return;
            DialogResult = DialogResult.OK;
        };

        Controls.AddRange([_parentLabel, nameLabel, _nameBox, _okButton, _cancelButton]);
        Load += (_, _) => _nameBox.Focus();
    }
}
