using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// Quick-open command palette.  Native ListView with Title / Category / Shortcut columns
/// so screen readers see real column text.  Type-ahead search box filters the list.
/// </summary>
public sealed class CommandPaletteForm : Form
{
    private readonly CommandPaletteViewModel _vm;
    private readonly TextBox   _searchBox;
    private readonly ListView  _commandList;
    private readonly List<CommandDefinition> _allCommands;

    public CommandDefinition? SelectedCommand { get; private set; }

    public CommandPaletteForm(ICommandRegistry registry)
    {
        _vm = new CommandPaletteViewModel(registry);
        _allCommands = [.. _vm.Commands];

        Text            = "Command Palette";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        ShowInTaskbar   = false;
        ClientSize      = new System.Drawing.Size(560, 440);
        StartPosition   = FormStartPosition.Manual;

        // ── Search box ────────────────────────────────────────────────────────────
        _searchBox = new TextBox
        {
            Dock           = DockStyle.Top,
            Height         = 28,
            Font           = new System.Drawing.Font(Font.FontFamily, 12f),
            AccessibleName = "Search commands",
        };

        // ── Command list (native ListView, Detail view) ───────────────────────────
        _commandList = new ListView
        {
            Dock           = DockStyle.Fill,
            View           = View.Details,
            FullRowSelect  = true,
            MultiSelect    = false,
            HideSelection  = false,
            HeaderStyle    = ColumnHeaderStyle.Nonclickable,
            AccessibleName = "Commands",
            AccessibleRole = AccessibleRole.List,
        };
        _commandList.Columns.Add("Command",  320);
        _commandList.Columns.Add("Category",  90);
        _commandList.Columns.Add("Shortcut", 110);

        Controls.Add(_commandList);
        Controls.Add(_searchBox);   // Top (rendered after Fill so it's on top)

        PopulateList(_allCommands);

        // ── Events ───────────────────────────────────────────────────────────────
        _searchBox.TextChanged += (_, _) =>
        {
            var q = _searchBox.Text.Trim();
            var filtered = string.IsNullOrEmpty(q)
                ? _allCommands
                : _allCommands.Where(c =>
                    c.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    c.Category.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            PopulateList(filtered);
        };

        _searchBox.KeyDown += (_, e) =>
        {
            switch (e.KeyCode)
            {
                case Keys.Down:
                    if (_commandList.Items.Count > 0)
                    {
                        _commandList.Focus();
                        SelectListItem(_commandList.SelectedIndices.Count == 0 ? 0
                            : Math.Min(_commandList.SelectedIndices[0] + 1, _commandList.Items.Count - 1));
                    }
                    e.Handled = true; break;
                case Keys.Up:
                    if (_commandList.Items.Count > 0)
                    {
                        _commandList.Focus();
                        SelectListItem(_commandList.SelectedIndices.Count == 0 ? 0
                            : Math.Max(_commandList.SelectedIndices[0] - 1, 0));
                    }
                    e.Handled = true; break;
                case Keys.Enter:
                    RunSelected(); e.Handled = true; break;
                case Keys.Escape:
                    DialogResult = DialogResult.Cancel; e.Handled = true; break;
            }
        };

        _commandList.KeyDown += (_, e) =>
        {
            switch (e.KeyCode)
            {
                case Keys.Enter:
                    RunSelected(); e.Handled = true; break;
                case Keys.Escape:
                    DialogResult = DialogResult.Cancel; e.Handled = true; break;
            }
        };

        _commandList.MouseDoubleClick += (_, _) => RunSelected();

        Load += OnLoad;
    }

    private void OnLoad(object? sender, EventArgs e)
    {
        if (Owner != null)
        {
            Left = Owner.Left + (Owner.Width - Width) / 2;
            Top  = Owner.Top + 60;
        }
        _searchBox.Focus();
    }

    private void PopulateList(IEnumerable<CommandDefinition> commands)
    {
        _commandList.BeginUpdate();
        _commandList.Items.Clear();
        foreach (var cmd in commands)
        {
            var item = new ListViewItem(cmd.Title);
            item.SubItems.Add(cmd.Category);
            item.SubItems.Add(cmd.GestureText);
            item.Tag = cmd;
            _commandList.Items.Add(item);
        }
        if (_commandList.Items.Count > 0)
            SelectListItem(0);
        _commandList.EndUpdate();
    }

    private void SelectListItem(int idx)
    {
        if (idx < 0 || idx >= _commandList.Items.Count) return;
        _commandList.Items[idx].Selected = true;
        _commandList.Items[idx].Focused  = true;
        _commandList.EnsureVisible(idx);
    }

    private void RunSelected()
    {
        if (_commandList.SelectedItems.Count == 0) return;
        if (_commandList.SelectedItems[0].Tag is CommandDefinition cmd)
        {
            SelectedCommand = cmd;
            DialogResult = DialogResult.OK;
            // Execute only after ShowDialog() returns — calling cmd.Execute() here
            // runs inside a closing form and crashes when commands open other dialogs.
        }
    }
}
