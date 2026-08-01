using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// Create/edit form for a server (Exchange/Graph) rule. Modeless (CLAUDE.md: editable text over the
/// live WebView2 reading pane must not run under a nested modal loop), so Escape/Cancel close it
/// explicitly. The owning <see cref="ServerRuleEditorViewModel"/> raises the save/close/folder-pick
/// requests as events; persistence is wired by the list VM that created the editor.
/// <para>
/// No F6 ring or Ctrl+Shift+P command palette here, and that is deliberate (#333, confirmed with the
/// screen-reader user): this is a single compact form — Name → conditions → action → Save/Cancel is
/// one linear Tab group with no distinct panes to cycle between, so F6 has nothing to jump to and a
/// palette would only duplicate the two visible buttons. The New Window Checklist's F6/palette items
/// apply to multi-pane windows; a leaf editor form is the documented exception.
/// </para>
/// </summary>
public partial class ServerRuleEditorWindow : Window
{
    private readonly ServerRuleEditorViewModel _vm;
    private readonly IEnumerable<AccountModel> _accounts;
    private readonly IReadOnlyDictionary<Guid, List<MailFolderModel>> _cachedFolders;

    public ServerRuleEditorWindow(
        ServerRuleEditorViewModel vm,
        IEnumerable<AccountModel> accounts,
        IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders)
    {
        _vm = vm;
        _accounts = accounts;
        _cachedFolders = cachedFolders;
        InitializeComponent();
        DataContext = vm;

        vm.PickFolderRequested += OnPickFolderRequested;
        vm.CloseRequested += OnCloseRequested;
        vm.AnnouncementRequested += OnAnnouncementRequested;

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            Keyboard.Focus(NameBox);
        };
    }

    private (string Id, string Name)? OnPickFolderRequested()
    {
        var picker = new FolderPickerWindow(_accounts, _cachedFolders, title: "Choose Folder") { Owner = this };
        if (picker.ShowDialog() == true && picker.SelectedFolder is MailFolderModel folder)
            return (folder.FullName, folder.DisplayName);
        return null;
    }

    private void OnCloseRequested() => Close();

    private void OnAnnouncementRequested(string text, AnnouncementCategory category)
        => AccessibilityHelper.Announce(this, text, category: category);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;

        // Modeless window: no DialogResult, so IsCancel doesn't auto-close on Escape. Wire it, but
        // step aside when a ComboBox dropdown is open so it can consume Escape itself.
        if (e.Key == Key.Escape && Keyboard.FocusedElement is not System.Windows.Controls.ComboBox { IsDropDownOpen: true })
        {
            Close();
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.PickFolderRequested -= OnPickFolderRequested;
        _vm.CloseRequested -= OnCloseRequested;
        _vm.AnnouncementRequested -= OnAnnouncementRequested;

        // Hand foreground back to the Rules Manager (our owner), not the main window. This window is
        // the active foreground window as it closes, so it may pass foreground to its owner — a
        // background window calling Activate() on itself would hit Windows' foreground lock and fail.
        // Without this the main window grabs activation and Tab operates there instead of the list.
        Owner?.Activate();

        base.OnClosed(e);
    }
}
