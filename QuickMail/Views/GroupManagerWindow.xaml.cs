using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.ViewModels;

namespace QuickMail.Views;

public partial class GroupManagerWindow : Window
{
    private readonly GroupManagerViewModel _vm;

    public GroupManagerWindow(GroupManagerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) =>
        {
            // Subscribe BEFORE LoadAsync so announcements and confirmations
            // raised during the first refresh are captured. (The dialog is
            // modal but the events are still safe — there's no nested message
            // loop that mutates WPF objects while a dialog is up.)
            vm.AnnouncementRequested += OnAnnouncement;
            vm.ConfirmRequested     += OnConfirm;
            await vm.LoadAsync();
            GroupsList.Focus();
        };
        Closed += (_, _) =>
        {
            // Always pair += with -= so the VM can be GC'd if the dialog
            // is short-lived (e.g. unit tests).
            vm.AnnouncementRequested -= OnAnnouncement;
            vm.ConfirmRequested     -= OnConfirm;
        };
    }

    private void OnAnnouncement(string text, AnnouncementCategory category)
    {
        AccessibilityHelper.Announce(this, text, category: category);
    }

    private Task<bool> OnConfirm(string title, string body) =>
        Task.FromResult(
            MessageBox.Show(this, body, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            == MessageBoxResult.Yes);

    // ── Keyboard wiring ──────────────────────────────────────────────────────

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Only the framework-level Esc-to-close shortcut lives here. All
        // user-facing actions are registered in the command registry (see
        // MainWindow.xaml.cs) and dispatched via the address book window
        // when this dialog is open.
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void NewGroupNameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            // If a group is selected, this acts as rename. If no group is
            // selected, it creates a new group. The VM command handles both
            // via the same NewName text box.
            _vm.NewName = NewGroupNameBox.Text;
            if (_vm.SelectedGroup is not null)
                _vm.RenameSelectedCommand.Execute(null);
            else
                _vm.CreateGroupCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void GroupsList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2)
        {
            NewGroupNameBox.Focus();
            NewGroupNameBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            if (_vm.SelectedGroup is not null)
            {
                _vm.DeleteSelectedCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    private void MembersList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && MembersList.SelectedItem is ContactModel c)
        {
            _vm.RemoveMemberCommand.Execute(c);
            e.Handled = true;
        }
    }

    private void CandidatesList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && CandidatesList.SelectedItem is ContactModel c)
        {
            // Toggle: adds if not yet a member, removes if already one.
            // This prevents repeated Enter presses from firing repeated "Added" announcements.
            _vm.ToggleMemberCommand.Execute(c);
            e.Handled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
