using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

public partial class SettingsDialog : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsDialog(SettingsViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        vm.DiagnosticsAnnouncementRequested += OnDiagnosticsAnnouncement;
        Closed += (_, _) => vm.DiagnosticsAnnouncementRequested -= OnDiagnosticsAnnouncement;
    }

    // Meta-announcement about screenshot capture (#175): force bypasses the
    // user's announcement preferences, matching the custom-announcements toggle.
    private void OnDiagnosticsAnnouncement(string text) =>
        AccessibilityHelper.Announce(this, text, interrupt: false, AnnouncementCategory.Status, force: true);

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Defer focus until after the window is activated so the screen reader picks it up.
        Dispatcher.InvokeAsync(() => PreviewLinesCombo.Focus(), DispatcherPriority.Input);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _vm.SaveCommand.Execute(null);
        LogService.Format  = _vm.LogFormat;
        LogService.Enabled = _vm.EnableLogging;

        // Applied live rather than at next startup: someone turning this on is troubleshooting a
        // problem that is happening now, and making them restart would lose the very thing they
        // are trying to capture.
        ConnectionJournal.Enabled = _vm.ConnectionDiagnostics;
        DialogResult = true;
        Close();
    }

    private void DeleteLogButton_Click(object sender, RoutedEventArgs e)
    {
        // Deletes BOTH logs. Someone deleting their logs means all of them — most often because the
        // logs hold their email addresses and mail server names and they don't want them on disk.
        // Leaving connection.log behind because it happens to be a second file would quietly defeat
        // that, and nothing in the UI would hint that a second log still existed.
        var result = MessageBox.Show(
            "Delete QuickMail's log files? This deletes the application log and the connection " +
            "diagnostics log, and cannot be undone.",
            "Delete Logs",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (result == MessageBoxResult.Yes)
        {
            LogService.DeleteLog();
            ConnectionJournal.DeleteLog();
        }

        DeleteLogButton.Focus();
    }

    private void HotkeyListView_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateActionButtons();
    }

    private void HotkeyListView_KeyDown(object sender, KeyEventArgs e)
    {
        if (_vm.SelectedHotkey == null) return;

        if (e.Key == Key.Return)
        {
            OpenSetShortcutDialog(_vm.SelectedHotkey);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _vm.SelectedHotkey.HasCustomBinding)
        {
            _vm.SelectedHotkey.ClearCustomBinding();
            UpdateActionButtons();
            e.Handled = true;
        }
    }

    private void SetShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedHotkey != null)
            OpenSetShortcutDialog(_vm.SelectedHotkey);
    }

    private void OpenSetShortcutDialog(SettingsViewModel.HotkeyRowViewModel row)
    {
        LogService.Debug($"[Settings] Opening capture dialog for '{row.CommandId}'");

        var captureDialog = new KeyCaptureDialog
        {
            Owner = this,
            ConflictChecker = (key, modifiers) =>
            {
                var conflict = _vm.FindConflict(key, modifiers);
                return (conflict != null && conflict != row) ? conflict.Title : null;
            },
        };

        if (captureDialog.ShowDialog() != true)
        {
            LogService.Debug("[Settings] Key capture cancelled");
            return;
        }

        var key       = captureDialog.CapturedKey;
        var modifiers = captureDialog.CapturedModifiers;
        var gesture   = GestureHelper.Format(key, modifiers);
        LogService.Debug($"[Settings] Captured: key={key} modifiers={modifiers} gesture='{gesture}'");

        // If the user accepted despite a conflict, clear the conflicting binding first.
        if (captureDialog.HasConflict)
        {
            var conflict = _vm.FindConflict(key, modifiers);
            if (conflict != null && conflict != row)
            {
                LogService.Debug($"[Settings] Reassigning from '{conflict.CommandId}'");
                conflict.ClearCustomBinding();
            }
        }

        row.SetCustomBinding(key, modifiers);
        UpdateActionButtons();
        LogService.Debug($"[Settings] Assigned '{gesture}' to '{row.Title}' — announcing");
        AnnounceAssignment(gesture, row.Title);
    }

    private void AnnounceAssignment(string gesture, string commandTitle)
    {
        var source = Keyboard.FocusedElement as UIElement ?? this;
        var text   = $"{gesture} assigned to {commandTitle}";

        LogService.Debug($"[Settings] Announce: source={source.GetType().Name} text='{text}'");

        try
        {
            AccessibilityHelper.Announce(source, text, interrupt: true);
            LogService.Debug("[Settings] Announce returned OK");
        }
        catch (Exception ex)
        {
            LogService.Log("[Settings] Announce failed", ex);
        }
    }

    private void RadioButton_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsLoaded: true } rb) return;
        var name = AutomationProperties.GetName(rb);
        if (string.IsNullOrEmpty(name))
            name = rb.Content?.ToString() ?? string.Empty;
        if (!string.IsNullOrEmpty(name))
            AccessibilityHelper.Announce(rb, name, category: AnnouncementCategory.Result);
    }

    private void RestoreDefaultButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedHotkey == null) return;
        _vm.SelectedHotkey.ClearCustomBinding();
        UpdateActionButtons();
    }

    private void UpdateActionButtons()
    {
        var row = _vm.SelectedHotkey;
        SetShortcutButton.IsEnabled    = row != null;
        RestoreDefaultButton.IsEnabled = row?.HasCustomBinding == true;
    }
}
