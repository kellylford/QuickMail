using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
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

    public RulesManagerWindow(
        RulesManagerViewModel vm,
        IEnumerable<AccountModel> accounts,
        IReadOnlyDictionary<Guid, List<MailFolderModel>> cachedFolders)
    {
        InitializeComponent();
        _vm = vm;
        _accounts = accounts;
        _cachedFolders = cachedFolders;
        DataContext = vm;

        // Wire VM events
        vm.CloseRequested += OnCloseRequested;
        vm.ConfirmDeleteRequested += OnConfirmDeleteRequested;
        vm.AnnouncementRequested += OnAnnouncementRequested;
        vm.PickFolderRequested += OnPickFolderRequested;

        // Focus the rule list on open
        Loaded += (_, _) => RuleListBox.Focus();
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

    protected override void OnClosed(EventArgs e)
    {
        _vm.CloseRequested -= OnCloseRequested;
        _vm.ConfirmDeleteRequested -= OnConfirmDeleteRequested;
        _vm.AnnouncementRequested -= OnAnnouncementRequested;
        _vm.PickFolderRequested -= OnPickFolderRequested;
        base.OnClosed(e);
    }
}
