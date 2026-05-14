using System;
using System.Windows.Forms;
using QuickMail.Models;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// Compose / reply / forward window backed by <see cref="ComposeViewModel"/>.
/// Uses native Win32 TextBox, ComboBox, and ListBox controls for full accessibility.
/// </summary>
public sealed class ComposeForm : Form
{
    private readonly ComposeViewModel _vm;

    private readonly TextBox   _toBox;
    private readonly TextBox   _ccBox;
    private readonly TextBox   _bccBox;
    private readonly TextBox   _subjectBox;
    private readonly ComboBox  _fromCombo;
    private readonly TextBox   _bodyBox;
    private readonly ListBox   _attachmentList;
    private readonly Label     _attachSummaryLabel;
    private readonly Label     _statusLabel;
    private readonly Button    _sendButton;
    private readonly Button    _saveDraftButton;
    private readonly Button    _attachButton;

    private bool _closingHandled;

    public ComposeForm(ComposeViewModel vm)
    {
        _vm = vm;

        Text            = "Compose Message";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize     = new System.Drawing.Size(560, 480);
        ClientSize      = new System.Drawing.Size(680, 540);
        StartPosition   = FormStartPosition.CenterParent;
        AllowDrop       = true;

        // ── Toolbar row ────────────────────────────────────────────────────────
        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        _sendButton     = new Button { Text = "&Send",       Width = 70, Height = 26 };
        _saveDraftButton = new Button { Text = "Save &Draft", Width = 80, Height = 26 };
        _attachButton   = new Button { Text = "&Attach…",    Width = 70, Height = 26 };

        var buttonPanel = new FlowLayoutPanel
        {
            Dock      = DockStyle.Top,
            Height    = 34,
            FlowDirection = FlowDirection.LeftToRight,
            Padding   = new Padding(4, 4, 0, 0),
        };
        buttonPanel.Controls.AddRange([_sendButton, _saveDraftButton, _attachButton]);

        // ── Header table ───────────────────────────────────────────────────────
        var headerPanel = new TableLayoutPanel
        {
            Dock        = DockStyle.Top,
            AutoSize    = true,
            ColumnCount = 2,
            RowCount    = 5,
            Padding     = new Padding(4, 0, 4, 0),
        };
        headerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        headerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _fromCombo = new ComboBox
        {
            Dock           = DockStyle.Fill,
            DropDownStyle  = ComboBoxStyle.DropDownList,
            AccessibleName = "From",
        };
        _toBox      = MakeHeaderBox("To");
        _ccBox      = MakeHeaderBox("Cc");
        _bccBox     = MakeHeaderBox("Bcc");
        _subjectBox = MakeHeaderBox("Subject");

        AddHeaderRow(headerPanel, "Fr&om:", _fromCombo);
        AddHeaderRow(headerPanel, "&To:",   _toBox);
        AddHeaderRow(headerPanel, "&Cc:",   _ccBox);
        AddHeaderRow(headerPanel, "&Bcc:",  _bccBox);
        AddHeaderRow(headerPanel, "S&ubject:", _subjectBox);

        // ── Body ──────────────────────────────────────────────────────────────
        _bodyBox = new TextBox
        {
            Dock           = DockStyle.Fill,
            Multiline      = true,
            ScrollBars     = ScrollBars.Vertical,
            AccessibleName = "Message body",
            WordWrap       = true,
        };

        // ── Attachments (bottom strip, hidden when empty) ──────────────────────
        var attachPanel = new Panel { Dock = DockStyle.Bottom, Height = 0 };
        _attachSummaryLabel = new Label
        {
            Dock     = DockStyle.Top,
            Height   = 18,
            Text     = string.Empty,
        };
        _attachmentList = new ListBox
        {
            Dock           = DockStyle.Fill,
            AccessibleName = "Attachments",
            SelectionMode  = SelectionMode.MultiSimple,
        };
        attachPanel.Controls.AddRange([_attachmentList, _attachSummaryLabel]);

        // ── Status bar ──────────────────────────────────────────────────────────
        var statusStrip = new StatusStrip();
        _statusLabel = new Label { Text = string.Empty, AutoSize = false, Width = 500 };
        var statusItem = new ToolStripControlHost(_statusLabel);
        statusStrip.Items.Add(statusItem);

        Controls.Add(_bodyBox);
        Controls.Add(attachPanel);
        Controls.Add(headerPanel);
        Controls.Add(buttonPanel);
        Controls.Add(statusStrip);

        // ── Wire events ────────────────────────────────────────────────────────
        _sendButton.Click     += async (_, _) => { await _vm.SendCommand.ExecuteAsync(null); };
        _saveDraftButton.Click += async (_, _) => { await _vm.SaveDraftCommand.ExecuteAsync(null); };
        _attachButton.Click   += (_, _) => _vm.AddAttachmentsCommand.Execute(null);

        _fromCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_fromCombo.SelectedItem is AccountModel acct)
                _vm.SenderAccount = acct;
        };

        _attachmentList.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete && _attachmentList.SelectedItem is AttachmentModel att)
            {
                _vm.RemoveAttachmentCommand.Execute(att);
                e.Handled = true;
            }
        };

        _toBox.TextChanged      += (_, _) => _vm.To      = _toBox.Text;
        _ccBox.TextChanged      += (_, _) => _vm.Cc      = _ccBox.Text;
        _bccBox.TextChanged     += (_, _) => _vm.Bcc     = _bccBox.Text;
        _subjectBox.TextChanged += (_, _) => _vm.Subject = _subjectBox.Text;
        _bodyBox.TextChanged    += (_, _) => _vm.Body    = _bodyBox.Text;

        // Drag-and-drop file attachment
        DragOver += (_, e) =>
            e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files)
                foreach (var f in files)
                    _vm.AddAttachmentFromPath(f);
        };

        // Keyboard shortcuts
        KeyPreview = true;
        KeyDown += OnKeyDown;

        // VM → Form sync
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.Attachments.CollectionChanged += (_, _) => RefreshAttachments(attachPanel);
        _vm.CloseRequested += () => { _closingHandled = true; Close(); };

        FormClosing += OnFormClosing;
        Load += (_, _) =>
        {
            PopulateFromAccounts();
            _toBox.Focus();
        };
    }

    private static TextBox MakeHeaderBox(string name) => new()
    {
        Dock           = DockStyle.Fill,
        AccessibleName = name,
    };

    private static void AddHeaderRow(TableLayoutPanel table, string labelText, Control field)
    {
        var lbl = new Label
        {
            Text      = labelText,
            Dock      = DockStyle.Fill,
            TextAlign = System.Drawing.ContentAlignment.MiddleRight,
        };
        table.Controls.Add(lbl);
        table.Controls.Add(field);
    }

    private void PopulateFromAccounts()
    {
        _fromCombo.BeginUpdate();
        _fromCombo.Items.Clear();
        foreach (var acct in _vm.SenderAccounts)
            _fromCombo.Items.Add(acct);
        _fromCombo.DisplayMember = nameof(AccountModel.DisplayName);
        if (_vm.SenderAccount != null)
            _fromCombo.SelectedItem = _vm.SenderAccount;
        else if (_fromCombo.Items.Count > 0)
            _fromCombo.SelectedIndex = 0;
        _fromCombo.EndUpdate();
    }

    private void RefreshAttachments(Panel attachPanel)
    {
        _attachmentList.BeginUpdate();
        _attachmentList.Items.Clear();
        foreach (var att in _vm.Attachments)
            _attachmentList.Items.Add(att);
        _attachmentList.DisplayMember = nameof(AttachmentModel.FileName);
        _attachmentList.EndUpdate();
        _attachSummaryLabel.Text = _vm.AttachmentSummaryText;
        var hasAtt = _vm.Attachments.Count > 0;
        attachPanel.Height = hasAtt ? 110 : 0;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(_vm.StatusText):
                _statusLabel.Text = _vm.StatusText;
                if (!string.IsNullOrEmpty(_vm.StatusText))
                    AccessibilityHelper.Announce(this, _vm.StatusText);
                break;
            case nameof(_vm.To):
                if (_toBox.Text != _vm.To) _toBox.Text = _vm.To;
                break;
            case nameof(_vm.Subject):
                if (_subjectBox.Text != _vm.Subject) _subjectBox.Text = _vm.Subject;
                break;
            case nameof(_vm.SenderAccount):
                if (_vm.SenderAccount != null && !_fromCombo.Items.Contains(_vm.SenderAccount))
                    PopulateFromAccounts();
                break;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyData == (Keys.Alt | Keys.S))
        {
            _vm.SendCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.KeyData == (Keys.Control | Keys.S))
        {
            _vm.SaveDraftCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.KeyData == (Keys.Control | Keys.Shift | Keys.A))
        {
            _vm.AddAttachmentsCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Escape)
        {
            _vm.CancelCommand.Execute(null);
            e.Handled = true;
        }
        // Ctrl+V with files on clipboard → add as attachments
        else if (e.KeyData == (Keys.Control | Keys.V))
        {
            var data = Clipboard.GetDataObject();
            if (data?.GetDataPresent(DataFormats.FileDrop) == true &&
                data.GetData(DataFormats.FileDrop) is string[] files)
            {
                foreach (var f in files)
                    _vm.AddAttachmentFromPath(f);
                e.Handled = true;
            }
        }
    }

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closingHandled || _vm.IsSent || !_vm.IsDirty) return;

        e.Cancel = true;

        var result = MessageBox.Show(
            this,
            "Do you want to save this message as a draft before closing?",
            "Save Draft?",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question);

        if (result == DialogResult.Cancel) return;

        if (result == DialogResult.No)
        {
            _closingHandled = true;
            Close();
            return;
        }

        // Yes → save draft first
        await _vm.SaveDraftCommand.ExecuteAsync(null);
        if (!_vm.StatusText.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            _closingHandled = true;
            Close();
        }
    }
}
