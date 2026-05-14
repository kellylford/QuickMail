using System;
using System.Windows.Forms;
using QuickMail.Models;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// Modal account manager: list of accounts on the left, edit form on the right.
/// </summary>
public sealed class AccountManagerForm : Form
{
    private readonly AccountManagerViewModel _vm;

    private readonly ListBox _accountList;
    private readonly Button _newButton;
    private readonly Button _deleteButton;
    private readonly Button _setDefaultButton;

    // Edit form controls
    private readonly Panel _editPanel;
    private readonly TextBox _displayNameBox;
    private readonly TextBox _usernameBox;
    private readonly TextBox _passwordBox;
    private readonly ComboBox _authTypeCombo;
    private readonly Panel _passwordPanel;
    private readonly Button _signInMsButton;
    private readonly TextBox _imapHostBox;
    private readonly TextBox _imapPortBox;
    private readonly CheckBox _imapSslCheck;
    private readonly CheckBox _imapInvalidCertCheck;
    private readonly TextBox _smtpHostBox;
    private readonly TextBox _smtpPortBox;
    private readonly CheckBox _smtpSslCheck;
    private readonly CheckBox _smtpInvalidCertCheck;
    private readonly Button _testButton;
    private readonly Button _saveButton;
    private readonly Label _statusLabel;

    private bool _suppressSync;

    public AccountManagerForm(AccountManagerViewModel vm)
    {
        _vm = vm;

        Text            = "Manage Accounts";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox     = false;
        ShowInTaskbar   = false;
        ClientSize      = new System.Drawing.Size(720, 560);
        StartPosition   = FormStartPosition.CenterParent;
        MinimumSize     = new System.Drawing.Size(580, 400);

        // ── Left panel: account list ──────────────────────────────────────────
        var leftPanel = new Panel
        {
            Dock  = DockStyle.Left,
            Width = 200,
        };

        _accountList = new ListBox
        {
            Dock           = DockStyle.Fill,
            AccessibleName = "Accounts",
            AccessibleRole = AccessibleRole.List,
            HorizontalScrollbar = false,
        };
        leftPanel.Controls.Add(_accountList);

        var leftButtons = new FlowLayoutPanel
        {
            Dock      = DockStyle.Bottom,
            Height    = 34,
            FlowDirection = FlowDirection.LeftToRight,
            Padding   = new Padding(2),
        };
        _newButton        = new Button { Text = "&New",    Width = 55, Height = 26 };
        _deleteButton     = new Button { Text = "Dele&te", Width = 55, Height = 26 };
        _setDefaultButton = new Button { Text = "De&fault", Width = 65, Height = 26 };
        leftButtons.Controls.AddRange([_newButton, _deleteButton, _setDefaultButton]);
        leftPanel.Controls.Add(leftButtons);

        // ── Splitter ──────────────────────────────────────────────────────────
        var splitter = new Splitter { Dock = DockStyle.Left, Width = 4 };

        // ── Right panel: edit form ────────────────────────────────────────────
        _editPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };

        int x = 10, y = 8, w = 460, lh = 18, th = 24, gap = 30;

        Label Lbl(string t) => new() { Text = t, Left = x, Top = y, Width = w, Height = lh, AutoSize = false };
        TextBox Tb(string name) => new() { Left = x, Top = y + lh, Width = w, Height = th, AccessibleName = name };

        _editPanel.Controls.Add(Lbl("&Display name:")); y += lh;
        _displayNameBox = Tb("Display name"); _editPanel.Controls.Add(_displayNameBox); y += gap;

        _editPanel.Controls.Add(Lbl("&Email address:")); y += lh;
        _usernameBox = Tb("Email address"); _editPanel.Controls.Add(_usernameBox); y += gap;

        _editPanel.Controls.Add(Lbl("&Authentication:")); y += lh;
        _authTypeCombo = new ComboBox
        {
            Left = x, Top = y, Width = w, Height = th,
            DropDownStyle  = ComboBoxStyle.DropDownList,
            AccessibleName = "Authentication type",
        };
        _authTypeCombo.Items.AddRange(["Password", "Microsoft OAuth2"]);
        _authTypeCombo.SelectedIndex = 0;
        _editPanel.Controls.Add(_authTypeCombo); y += gap;

        // Password panel
        _passwordPanel = new Panel { Left = x, Top = y, Width = w, Height = lh + th + 4 };
        var pwLbl = new Label { Text = "&Password:", Left = 0, Top = 0, Width = w, Height = lh };
        _passwordBox = new TextBox { Left = 0, Top = lh, Width = w, Height = th, UseSystemPasswordChar = true, AccessibleName = "Password" };
        _passwordPanel.Controls.AddRange([pwLbl, _passwordBox]);
        _editPanel.Controls.Add(_passwordPanel); y += lh + th + 10;

        // OAuth2 sign-in button (shown instead of password panel for OAuth2)
        _signInMsButton = new Button { Text = "&Sign in with Microsoft", Left = x, Top = y, Width = 200, Height = 28, Visible = false };
        _editPanel.Controls.Add(_signInMsButton); y += 36;

        // IMAP section
        var imapHeader = new Label { Text = "── IMAP settings ──", Left = x, Top = y, Width = w, Height = lh, AutoSize = false };
        _editPanel.Controls.Add(imapHeader); y += lh + 4;

        _editPanel.Controls.Add(Lbl("IMAP &host:")); y += lh;
        _imapHostBox = Tb("IMAP host"); _editPanel.Controls.Add(_imapHostBox); y += gap;
        _editPanel.Controls.Add(Lbl("IMAP &port:")); y += lh;
        _imapPortBox = new TextBox { Left = x, Top = y, Width = 80, Height = th, AccessibleName = "IMAP port" };
        _editPanel.Controls.Add(_imapPortBox); y += gap;
        _imapSslCheck = new CheckBox { Text = "Use SS&L/TLS", Left = x, Top = y, Width = w };
        _editPanel.Controls.Add(_imapSslCheck); y += 26;
        _imapInvalidCertCheck = new CheckBox { Text = "Accept &invalid certificates (IMAP)", Left = x, Top = y, Width = w };
        _editPanel.Controls.Add(_imapInvalidCertCheck); y += gap;

        // SMTP section
        var smtpHeader = new Label { Text = "── SMTP settings ──", Left = x, Top = y, Width = w, Height = lh, AutoSize = false };
        _editPanel.Controls.Add(smtpHeader); y += lh + 4;

        _editPanel.Controls.Add(Lbl("S&MTP host:")); y += lh;
        _smtpHostBox = Tb("SMTP host"); _editPanel.Controls.Add(_smtpHostBox); y += gap;
        _editPanel.Controls.Add(Lbl("SMTP p&ort:")); y += lh;
        _smtpPortBox = new TextBox { Left = x, Top = y, Width = 80, Height = th, AccessibleName = "SMTP port" };
        _editPanel.Controls.Add(_smtpPortBox); y += gap;
        _smtpSslCheck = new CheckBox { Text = "Use SS&L/TLS (SMTP)", Left = x, Top = y, Width = w };
        _editPanel.Controls.Add(_smtpSslCheck); y += 26;
        _smtpInvalidCertCheck = new CheckBox { Text = "Accept invalid certificates (S&MTP)", Left = x, Top = y, Width = w };
        _editPanel.Controls.Add(_smtpInvalidCertCheck); y += gap;

        // Status and buttons
        _statusLabel = new Label { Left = x, Top = y, Width = w, Height = 20, ForeColor = System.Drawing.Color.DarkRed };
        _editPanel.Controls.Add(_statusLabel); y += 24;

        _testButton = new Button { Text = "&Test connection", Left = x, Top = y, Width = 130, Height = 26 };
        _saveButton = new Button { Text = "&Save",           Left = x + 140, Top = y, Width = 80, Height = 26 };
        _editPanel.Controls.Add(_testButton);
        _editPanel.Controls.Add(_saveButton);

        // Close button at bottom of window
        var closeButton = new Button
        {
            Text   = "&Close",
            Dock   = DockStyle.Bottom,
            Height = 30,
            DialogResult = DialogResult.OK,
        };
        AcceptButton = closeButton;
        CancelButton = closeButton;

        Controls.Add(_editPanel);
        Controls.Add(splitter);
        Controls.Add(leftPanel);
        Controls.Add(closeButton);

        // ── Populate and wire events ──────────────────────────────────────────
        RebuildAccountList();

        _accountList.SelectedIndexChanged += OnAccountSelected;
        _authTypeCombo.SelectedIndexChanged += OnAuthTypeChanged;

        _newButton.Click    += OnNew;
        _deleteButton.Click += OnDelete;
        _setDefaultButton.Click += (_, _) => _vm.SetDefaultCommand.Execute(_vm.SelectedAccount);
        _testButton.Click   += OnTest;
        _saveButton.Click   += OnSave;
        _signInMsButton.Click += OnSignInMs;

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_vm.StatusText))
                _statusLabel.Text = _vm.StatusText;

            if (e.PropertyName == nameof(_vm.Accounts))
                RebuildAccountList();
        };

        SetEditPanelEnabled(false);
        Load += (_, _) => _accountList.Focus();
    }

    private void RebuildAccountList()
    {
        _accountList.BeginUpdate();
        _accountList.Items.Clear();
        foreach (var account in _vm.Accounts)
            _accountList.Items.Add(account.IsDefault ? $"{account.DisplayName} (default)" : account.DisplayName);
        _accountList.EndUpdate();
    }

    private void OnAccountSelected(object? sender, EventArgs e)
    {
        var idx = _accountList.SelectedIndex;
        if (idx < 0 || idx >= _vm.Accounts.Count) { SetEditPanelEnabled(false); return; }

        _vm.SelectedAccount = _vm.Accounts[idx];
        SetEditPanelEnabled(true);
        PopulateForm();
    }

    private void PopulateForm()
    {
        _suppressSync = true;
        _displayNameBox.Text       = _vm.DisplayName;
        _usernameBox.Text          = _vm.Username;
        _authTypeCombo.SelectedIndex = _vm.AuthTypeIndex;
        _passwordBox.Text          = _vm.Password;
        _imapHostBox.Text          = _vm.ImapHost;
        _imapPortBox.Text          = _vm.ImapPort.ToString();
        _imapSslCheck.Checked      = _vm.ImapUseSsl;
        _imapInvalidCertCheck.Checked = _vm.ImapAcceptInvalidCert;
        _smtpHostBox.Text          = _vm.SmtpHost;
        _smtpPortBox.Text          = _vm.SmtpPort.ToString();
        _smtpSslCheck.Checked      = _vm.SmtpUseSsl;
        _smtpInvalidCertCheck.Checked = _vm.SmtpAcceptInvalidCert;
        _statusLabel.Text          = string.Empty;
        UpdateAuthVisibility();
        _suppressSync = false;
    }

    private void OnAuthTypeChanged(object? sender, EventArgs e)
    {
        if (_suppressSync) return;
        _vm.AuthTypeIndex = _authTypeCombo.SelectedIndex;
        UpdateAuthVisibility();
    }

    private void UpdateAuthVisibility()
    {
        var isPassword = _authTypeCombo.SelectedIndex == 0;
        _passwordPanel.Visible   = isPassword;
        _signInMsButton.Visible  = !isPassword;
    }

    private void SyncFormToVm()
    {
        _vm.DisplayName           = _displayNameBox.Text;
        _vm.Username              = _usernameBox.Text;
        _vm.AuthTypeIndex         = _authTypeCombo.SelectedIndex;
        _vm.Password              = _passwordBox.Text;
        _vm.ImapHost              = _imapHostBox.Text;
        _vm.ImapPort              = int.TryParse(_imapPortBox.Text, out var ip) ? ip : 993;
        _vm.ImapUseSsl            = _imapSslCheck.Checked;
        _vm.ImapAcceptInvalidCert = _imapInvalidCertCheck.Checked;
        _vm.SmtpHost              = _smtpHostBox.Text;
        _vm.SmtpPort              = int.TryParse(_smtpPortBox.Text, out var sp) ? sp : 587;
        _vm.SmtpUseSsl            = _smtpSslCheck.Checked;
        _vm.SmtpAcceptInvalidCert = _smtpInvalidCertCheck.Checked;
    }

    private void SetEditPanelEnabled(bool enabled)
    {
        foreach (Control c in _editPanel.Controls)
            if (c != _statusLabel)
                c.Enabled = enabled;
    }

    private void OnNew(object? sender, EventArgs e)
    {
        var addVm     = _vm.CreateAddAccountViewModel();
        using var dlg = new AddAccountForm(addVm) { Owner = this };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        _vm.CommitNewAccount(addVm.ToAccountModel(), dlg.Password);
        RebuildAccountList();
        _accountList.SelectedIndex = _vm.Accounts.Count - 1;
    }

    private void OnDelete(object? sender, EventArgs e)
    {
        _vm.DeleteAccountCommand.Execute(null);
        RebuildAccountList();
    }

    private async void OnTest(object? sender, EventArgs e)
    {
        SyncFormToVm();
        _statusLabel.Text = "Testing…";
        await _vm.TestConnectionCommand.ExecuteAsync(null);
        _statusLabel.Text = _vm.StatusText;
    }

    private void OnSave(object? sender, EventArgs e)
    {
        SyncFormToVm();
        _vm.SaveAccountCommand.Execute(null);
        RebuildAccountList();
    }

    private async void OnSignInMs(object? sender, EventArgs e)
    {
        SyncFormToVm();
        await _vm.SignInMicrosoftCommand.ExecuteAsync(null);
        _usernameBox.Text = _vm.Username;
        _statusLabel.Text = _vm.StatusText;
    }
}
