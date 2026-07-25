using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using QuickMail.Models;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>Inverts a boolean value. Used for IsReadOnly/IsTabStop bindings.</summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : true;
}

public partial class RulesManagerWindow : Window
{
    private readonly RulesManagerViewModel _vm;
    private readonly IEnumerable<AccountModel> _accounts;
    private readonly IReadOnlyDictionary<Guid, List<MailFolderModel>> _cachedFolders;

    private readonly ServerRulesViewModel? _serverRulesVm;

    public RulesManagerWindow(
        RulesManagerViewModel vm,
        IEnumerable<AccountModel> accounts,
        IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders,
        ServerRulesViewModel? serverRulesVm = null)
    {
        InitializeComponent();
        _vm = vm;
        _accounts = accounts;
        _cachedFolders = cachedFolders;
        _serverRulesVm = serverRulesVm;
        DataContext = vm;

        // Wire VM events
        vm.CloseRequested += OnCloseRequested;
        vm.ConfirmDeleteRequested += OnConfirmDeleteRequested;
        vm.AnnouncementRequested += OnAnnouncementRequested;
        vm.PickFolderRequested += OnPickFolderRequested;

        // Read-only server-rules section (#333): a distinct sub-tree with its own DataContext so its
        // bindings resolve against the ServerRulesViewModel rather than the client-rules VM. Hidden
        // entirely when there's no Graph account.
        if (_serverRulesVm is not null)
        {
            ServerRulesSection.DataContext = _serverRulesVm;
            ServerRulesSection.Visibility = Visibility.Visible;
            _serverRulesVm.AnnouncementRequested += OnAnnouncementRequested;
            _serverRulesVm.WriteBlockedByPermission += OnServerRulesPermissionMessage;
            // Load the account's server rules, THEN decide focus — landing focus on the list that
            // actually holds the user's rules (they may have only server rules, no client rules).
            Loaded += async (_, _) =>
            {
                await _serverRulesVm.RefreshCommand.ExecuteAsync(null);
                FocusInitialList();
            };
        }
        else
        {
            // No server rules in play: original behaviour — focus the client rule list on open (#348).
            Loaded += (_, _) => FocusFirstRule();
        }
    }

    /// <summary>
    /// Lands focus on whichever list has content. A Graph account with no client rules would
    /// otherwise open onto the empty client list and sound empty while the user's (server) rules sit
    /// in the section below.
    /// </summary>
    private void FocusInitialList()
    {
        if (RuleListBox.Items.Count > 0 || ServerRulesListBox.Items.Count == 0)
        {
            FocusFirstRule();
            return;
        }

        ServerRulesListBox.SelectedIndex = 0;
        ServerRulesListBox.UpdateLayout();
        if (ServerRulesListBox.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem item)
            item.Focus();
        else
            ServerRulesListBox.Focus();
    }

    private void OnServerRulesPermissionMessage(string message)
        => AccessibilityHelper.Announce(this, message, category: AnnouncementCategory.Hint);

    /// <summary>Moves keyboard focus to the next (or previous) window pane for F6 / Shift+F6.</summary>
    private void CyclePane(bool forward)
    {
        // Only include panes that are actually present/visible.
        var stops = new List<UIElement> { RuleListBox };
        if (_serverRulesVm is not null && ServerRulesSection.Visibility == Visibility.Visible)
        {
            stops.Add(ServerRulesListBox);
            stops.Add(ServerRulesDetailBox);
            stops.Add(ServerRulesStatusText);
        }
        stops.Add(MainStatusText);

        // Find where focus currently sits (walk up from the focused element to a known pane).
        var current = -1;
        for (var node = Keyboard.FocusedElement as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement el && (current = stops.IndexOf(el)) >= 0) break;
        }

        var next = current < 0
            ? 0
            : (current + (forward ? 1 : stops.Count - 1)) % stops.Count;
        stops[next].Focus();
    }

    private void FocusFirstRule()
    {
        if (RuleListBox.Items.Count == 0)
        {
            RuleListBox.Focus();
            return;
        }

        if (RuleListBox.SelectedIndex < 0)
            RuleListBox.SelectedIndex = 0;

        RuleListBox.UpdateLayout();
        if (RuleListBox.ItemContainerGenerator.ContainerFromIndex(RuleListBox.SelectedIndex) is ListBoxItem item)
            item.Focus();
        else
            RuleListBox.Focus();
    }

    private string? OnPickFolderRequested()
    {
        var picker = new FolderPickerWindow(
            _accounts,
            _cachedFolders,
            title: "Choose Target Folder") { Owner = this };

        if (picker.ShowDialog() == true && picker.SelectedFolder is MailFolderModel folder)
        {
            return folder.FullName;
        }
        return null;
    }

    private void OnCloseRequested()
    {
        Close();
    }

    /// <summary>
    /// Adds a rule prefilled from a message and focuses the list. Called when Ctrl+Shift+T is
    /// pressed while this (modeless) window is already open, so the template isn't dropped.
    /// </summary>
    public void PrefillFromTemplate(MailRule template)
    {
        _vm.AddPrefilledRule(template);
        RuleListBox.Focus();
    }

    private bool OnConfirmDeleteRequested(string message, string title)
    {
        return MessageBox.Show(
            message, title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private void OnAnnouncementRequested(string text, AnnouncementCategory category)
    {
        AccessibilityHelper.Announce(this, text, interrupt: true, category: category);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Dialog-local shortcuts (not registered in CommandRegistry — these are
        // scoped to this window only, same pattern as other dialogs).
        if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _vm.NewRuleCommand.Execute(null);
            e.Handled = true;
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;

        // This window is shown modeless (see MainWindow.OpenRulesManager) to avoid the
        // modal-over-WebView2 dispatcher deadlock. A modeless window has no DialogResult,
        // so the Close button's IsCancel="True" no longer closes it on Escape — wire that
        // explicitly here. Step aside when an open combo dropdown needs Escape to dismiss
        // itself, so we don't steal it (matches ComposeWindow's guard).
        // F6 / Shift+F6 cycle between the window's panes (New Window Checklist). Stops are the client
        // rule list, the server-rules list and detail (when shown), and the status line — so a
        // keyboard/screen-reader user can reach every region, including the status to re-read counts.
        if (e.Key == Key.F6)
        {
            CyclePane(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (AccountScopeCombo.IsDropDownOpen || ActionCombo.IsDropDownOpen)
                return;

            Close();
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.CloseRequested -= OnCloseRequested;
        _vm.ConfirmDeleteRequested -= OnConfirmDeleteRequested;
        _vm.AnnouncementRequested -= OnAnnouncementRequested;
        _vm.PickFolderRequested -= OnPickFolderRequested;
        if (_serverRulesVm is not null)
        {
            _serverRulesVm.AnnouncementRequested -= OnAnnouncementRequested;
            _serverRulesVm.WriteBlockedByPermission -= OnServerRulesPermissionMessage;
        }
        base.OnClosed(e);
    }
}
