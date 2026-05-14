using System;
using System.Windows.Forms;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// Dialog for adding a new mail account.  Collects display name, credentials, IMAP and SMTP settings.
/// </summary>
public sealed class AddAccountForm : Form
{
    private readonly AddAccountViewModel _vm;

    // Account fields
    private readonly TextBox _displayNameBox;
    private readonly TextBox _usernameBox;
    private readonly TextBox _passwordBox;

    // IMAP fields
    private readonly TextBox _imapHostBox;
    private readonly TextBox _imapPortBox;
    private readonly CheckBox _imapSslCheck;
    private readonly CheckBox _imapInvalidCertCheck;

    // SMTP fields
    private readonly TextBox _smtpHostBox;
    private readonly TextBox _smtpPortBox;
    private readonly CheckBox _smtpSslCheck;
    private readonly CheckBox _smtpInvalidCertCheck;

    // Auth
    private readonly ComboBox _authTypeCombo;
    private readonly Panel _passwordPanel;

    // Status
    private readonly Label _statusLabel;
    private readonly Button _testButton;
    private readonly Button _addButton;
    private readonly Button _cancelButton;

    public string Password => _passwordBox.Text;

    public AddAccountForm(AddAccountViewModel vm)
    {
        _vm = vm;

        Text            = "Add Account";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        ShowInTaskbar   = false;
        ClientSize      = new System.Drawing.Size(440, 560);
        StartPosition   = FormStartPosition.CenterParent;
        AutoScroll      = true;

        int x = 12, y = 10, w = 416, lh = 24, th = 24, gap = 32;

        Label MakeLabel(string text) => new() { Text = text, Left = x, Top = y, Width = w, Height = lh };
        TextBox MakeBox(string name, bool isPassword = false)
        {
            var tb = new TextBox { Left = x, Top = y + lh, Width = w, Height = th, AccessibleName = name };
            if (isPassword) { tb.UseSystemPasswordChar = true; }
            return tb;
        }

        // Display name
        Controls.Add(MakeLabel("&Display name:"));
        y += lh;
        _displayNameBox = MakeBox("Display name"); Controls.Add(_displayNameBox); y += gap;

        // Username
        Controls.Add(MakeLabel("&Email address:"));
        y += lh;
        _usernameBox = MakeBox("Email address"); Controls.Add(_usernameBox); y += gap;

        // Auth type
        Controls.Add(MakeLabel("&Authentication:"));
        y += lh;
        _authTypeCombo = new ComboBox
        {
            Left = x, Top = y, Width = w, Height = th, DropDownStyle = ComboBoxStyle.DropDownList,
            AccessibleName = "Authentication type",
        };
        _authTypeCombo.Items.AddRange(["Password", "Microsoft OAuth2"]);
        _authTypeCombo.SelectedIndex = 0;
        Controls.Add(_authTypeCombo); y += gap;

        // Password panel (hidden when OAuth2 selected)
        _passwordPanel = new Panel { Left = x, Top = y, Width = w, Height = gap + lh };
        var pwLabel = new Label { Text = "&Password:", Left = 0, Top = 0, Width = w, Height = lh };
        _passwordBox = new TextBox { Left = 0, Top = lh, Width = w, Height = th, UseSystemPasswordChar = true, AccessibleName = "Password" };
        _passwordPanel.Controls.AddRange([pwLabel, _passwordBox]);
        Controls.Add(_passwordPanel); y += gap + lh + 4;

        // IMAP section
        Controls.Add(new Label { Text = "── IMAP settings ──", Left = x, Top = y, Width = w, Height = lh, AutoSize = false }); y += lh + 4;

        Controls.Add(MakeLabel("&IMAP host:"));
        y += lh;
        _imapHostBox = MakeBox("IMAP host"); Controls.Add(_imapHostBox); y += gap;

        Controls.Add(MakeLabel("IMAP &port:"));
        y += lh;
        _imapPortBox = new TextBox { Left = x, Top = y, Width = 80, Text = "993", AccessibleName = "IMAP port" };
        Controls.Add(_imapPortBox); y += gap;

        _imapSslCheck = new CheckBox { Text = "Use &SSL/TLS", Left = x, Top = y, Width = w, Checked = true };
        Controls.Add(_imapSslCheck); y += 26;
        _imapInvalidCertCheck = new CheckBox { Text = "Accept &invalid certificates", Left = x, Top = y, Width = w };
        Controls.Add(_imapInvalidCertCheck); y += gap;

        // SMTP section
        Controls.Add(new Label { Text = "── SMTP settings ──", Left = x, Top = y, Width = w, Height = lh, AutoSize = false }); y += lh + 4;

        Controls.Add(MakeLabel("S&MTP host:"));
        y += lh;
        _smtpHostBox = MakeBox("SMTP host"); Controls.Add(_smtpHostBox); y += gap;

        Controls.Add(MakeLabel("SMTP p&ort:"));
        y += lh;
        _smtpPortBox = new TextBox { Left = x, Top = y, Width = 80, Text = "587", AccessibleName = "SMTP port" };
        Controls.Add(_smtpPortBox); y += gap;

        _smtpSslCheck = new CheckBox { Text = "Use SS&L/TLS", Left = x, Top = y, Width = w };
        Controls.Add(_smtpSslCheck); y += 26;
        _smtpInvalidCertCheck = new CheckBox { Text = "Accept i&nvalid certificates (SMTP)", Left = x, Top = y, Width = w };
        Controls.Add(_smtpInvalidCertCheck); y += gap;

        // Status
        _statusLabel = new Label { Left = x, Top = y, Width = w, Height = 20, ForeColor = System.Drawing.Color.DarkRed };
        Controls.Add(_statusLabel); y += 22;

        // Buttons
        _testButton = new Button { Text = "&Test connection", Left = x, Top = y, Width = 130, Height = 26 };
        _addButton  = new Button { Text = "&Add", Left = 238, Top = y, Width = 80, Height = 26 };
        _cancelButton = new Button { Text = "Cancel", Left = 326, Top = y, Width = 80, Height = 26, DialogResult = DialogResult.Cancel };
        Controls.AddRange([_testButton, _addButton, _cancelButton]);

        AcceptButton = _addButton;
        CancelButton = _cancelButton;

        // Wire events
        _authTypeCombo.SelectedIndexChanged += (_, _) =>
            _passwordPanel.Visible = _authTypeCombo.SelectedIndex == 0;

        _testButton.Click += async (_, _) =>
        {
            SyncToVm();
            _vm.Password = _passwordBox.Text;
            _statusLabel.Text = "Testing…";
            await _vm.TestConnectionCommand.ExecuteAsync(null);
            _statusLabel.Text = _vm.StatusText;
        };

        _addButton.Click += (_, _) =>
        {
            SyncToVm();
            _vm.Password = _passwordBox.Text;
            DialogResult = DialogResult.OK;
        };

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_vm.StatusText))
                _statusLabel.Text = _vm.StatusText;
        };

        Load += (_, _) => _displayNameBox.Focus();
    }

    private void SyncToVm()
    {
        _vm.DisplayName           = _displayNameBox.Text;
        _vm.Username              = _usernameBox.Text;
        _vm.ImapHost              = _imapHostBox.Text;
        _vm.ImapPort              = int.TryParse(_imapPortBox.Text, out var ip) ? ip : 993;
        _vm.ImapUseSsl            = _imapSslCheck.Checked;
        _vm.ImapAcceptInvalidCert = _imapInvalidCertCheck.Checked;
        _vm.SmtpHost              = _smtpHostBox.Text;
        _vm.SmtpPort              = int.TryParse(_smtpPortBox.Text, out var sp) ? sp : 587;
        _vm.SmtpUseSsl            = _smtpSslCheck.Checked;
        _vm.SmtpAcceptInvalidCert = _smtpInvalidCertCheck.Checked;
        _vm.AuthTypeIndex         = _authTypeCombo.SelectedIndex;
    }
}
