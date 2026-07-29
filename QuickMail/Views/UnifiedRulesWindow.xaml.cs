using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// The unified single-list rules manager (spec §20.7): one account picker over all accounts, one list
/// of the account's server + client rules, driven by <see cref="UnifiedRulesViewModel"/>. Modeless —
/// the editor it launches contains editable text over the reading pane, which must not run under a
/// nested modal loop (the GrabAddresses rule).
/// </summary>
public partial class UnifiedRulesWindow : Window
{
    private readonly UnifiedRulesViewModel _vm;
    private readonly IEnumerable<AccountModel> _accounts;
    private readonly IReadOnlyDictionary<Guid, List<MailFolderModel>> _cachedFolders;

    public UnifiedRulesWindow(
        UnifiedRulesViewModel vm,
        IEnumerable<AccountModel> accounts,
        IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders)
    {
        InitializeComponent();
        _vm = vm;
        _accounts = accounts;
        _cachedFolders = cachedFolders;
        DataContext = vm;

        vm.EditorRequested += OnEditorRequested;
        vm.ConfirmDeleteRequested += OnConfirmDelete;
        vm.ClientRuleNoticeRequested += OnClientRuleNotice;
        vm.AnnouncementRequested += OnAnnouncement;
        vm.WriteBlockedByPermission += OnPermissionMessage;
        vm.FocusSelectedRuleRequested += OnFocusSelectedRule;

        Loaded += async (_, _) =>
        {
            await vm.RefreshCommand.ExecuteAsync(null);
            FocusInitialControl();
        };
    }

    // The account picker is the intended first landing spot (spec §20.7) — land there when it's shown
    // (more than one account); otherwise there's no choice to make, so land on the rule list.
    private void FocusInitialControl()
    {
        if (AccountPicker.Visibility == Visibility.Visible)
        {
            AccountCombo.Focus();
            Keyboard.Focus(AccountCombo);
        }
        else
        {
            FocusFirstRule();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void OnEditorRequested(ServerRuleEditorViewModel editorVm)
    {
        var editor = new ServerRuleEditorWindow(editorVm, _accounts, _cachedFolders) { Owner = this };
        editor.Show();
        editor.Activate();
    }

    private bool OnConfirmDelete(string message, string title)
        => MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private void OnClientRuleNotice(string message)
        => MessageBox.Show(this, message, "Saved as a QuickMail rule", MessageBoxButton.OK, MessageBoxImage.Information);

    private void OnAnnouncement(string text, AnnouncementCategory category)
        => AccessibilityHelper.Announce(this, text, interrupt: true, category: category);

    private void OnPermissionMessage(string message)
        => AccessibilityHelper.Announce(this, message, category: AnnouncementCategory.Hint);

    // Enter = edit, Space = enable/disable, Delete = delete. Each honours the command's CanExecute, so
    // a key does nothing where the button/menu item is disabled (e.g. Move on a client rule).
    private void RulesListBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter: Invoke(_vm.EditRuleCommand); e.Handled = true; break;
            case Key.Space: Invoke(_vm.ToggleEnabledCommand); e.Handled = true; break;
            case Key.Delete: Invoke(_vm.DeleteRuleCommand); e.Handled = true; break;
        }

        static void Invoke(System.Windows.Input.ICommand cmd)
        {
            if (cmd.CanExecute(null)) cmd.Execute(null);
        }
    }

    // Return focus to the selected rule after a write, so the screen reader re-reads the row (with its
    // new state/position) and arrow keys stay in the list. Deferred + container-based (see the notes
    // that shaped the server-section version).
    private void OnFocusSelectedRule()
        => Dispatcher.BeginInvoke(new Action(FocusSelectedRuleNow), DispatcherPriority.Input);

    private void FocusSelectedRuleNow()
    {
        if (_vm.SelectedRule is not { } rule) return;
        var index = RulesListBox.Items.IndexOf(rule);
        if (index < 0) return;
        RulesListBox.ScrollIntoView(rule);
        RulesListBox.UpdateLayout();
        if (RulesListBox.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item)
        {
            item.Focus();
            Keyboard.Focus(item);
        }
    }

    private void FocusFirstRule()
    {
        if (RulesListBox.Items.Count == 0)
        {
            RulesListBox.Focus();
            return;
        }
        if (RulesListBox.SelectedIndex < 0) RulesListBox.SelectedIndex = 0;
        RulesListBox.UpdateLayout();
        if (RulesListBox.ItemContainerGenerator.ContainerFromIndex(RulesListBox.SelectedIndex) is ListBoxItem item)
            item.Focus();
        else
            RulesListBox.Focus();
    }

    /// <summary>F6 / Shift+F6 cycle the panes: account picker (when shown) → list → detail → status.</summary>
    private void CyclePane(bool forward)
    {
        var stops = new List<UIElement>();
        if (AccountPicker.Visibility == Visibility.Visible) stops.Add(AccountCombo);
        stops.Add(RulesListBox);
        stops.Add(DetailBox);
        stops.Add(StatusText);

        var current = -1;
        for (var node = Keyboard.FocusedElement as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement el && (current = stops.IndexOf(el)) >= 0) break;

        var next = current < 0 ? 0 : (current + (forward ? 1 : stops.Count - 1)) % stops.Count;
        stops[next].Focus();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;

        if (e.Key == Key.F6)
        {
            CyclePane(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0);
            e.Handled = true;
            return;
        }

        // Modeless window: no DialogResult, so wire Escape to Close — but step aside for an open combo
        // dropdown that needs Escape to dismiss itself.
        if (e.Key == Key.Escape && !(Keyboard.FocusedElement is ComboBox { IsDropDownOpen: true }))
        {
            Close();
            e.Handled = true;
        }
    }

    // Ctrl+N opens a new rule (window-scoped, same pattern as the other dialogs).
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (_vm.NewRuleCommand.CanExecute(null)) _vm.NewRuleCommand.Execute(null);
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.EditorRequested -= OnEditorRequested;
        _vm.ConfirmDeleteRequested -= OnConfirmDelete;
        _vm.ClientRuleNoticeRequested -= OnClientRuleNotice;
        _vm.AnnouncementRequested -= OnAnnouncement;
        _vm.WriteBlockedByPermission -= OnPermissionMessage;
        _vm.FocusSelectedRuleRequested -= OnFocusSelectedRule;
        base.OnClosed(e);
    }
}
