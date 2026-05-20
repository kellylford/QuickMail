using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using QuickMail.ViewModels;

namespace QuickMail.Views;

public partial class ViewManagerWindow : Window
{
    private readonly ViewManagerViewModel _vm;

    public ViewManagerWindow(ViewManagerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;

        vm.FolderConflictDetected += OnFolderConflict;
        vm.SetHotkeyRequested     += OnSetHotkeyRequested;
    }

    // ── Folder conflict prompt ────────────────────────────────────────────────────

    private void OnFolderConflict(object? sender, FolderConflictEventArgs e)
    {
        var existing = string.Join(", ", e.ExistingFolders);
        var msg = $"The current folder ({e.NewFolder}) is not in this view's folder list ({existing}).\n\n" +
                  $"Yes = replace the existing folder(s) with {e.NewFolder}\n" +
                  $"No  = add {e.NewFolder} alongside the existing folder(s)\n" +
                  $"Cancel = do nothing";

        var result = MessageBox.Show(msg, "Folder Changed",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        var resolution = result switch
        {
            MessageBoxResult.Yes    => FolderConflictResolution.Replace,
            MessageBoxResult.No     => FolderConflictResolution.Add,
            _                       => FolderConflictResolution.Cancel,
        };

        _vm.ResolveConflict(resolution);
    }

    // ── Hotkey capture ────────────────────────────────────────────────────────────

    private void OnSetHotkeyRequested(object? sender, EventArgs e)
    {
        var dlg = new KeyCaptureDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;

        // Check for conflicts across the entire registry (same approach as SettingsDialog).
        var key       = dlg.CapturedKey;
        var modifiers = dlg.CapturedModifiers;
        var gesture   = Helpers.GestureHelper.Format(key, modifiers);

        // Check all existing views for the same hotkey (exclude the one being edited).
        var conflict = _vm.SavedViews.FirstOrDefault(v =>
            v != _vm.SelectedView &&
            !string.IsNullOrEmpty(v.Hotkey) &&
            string.Equals(v.Hotkey, gesture, StringComparison.OrdinalIgnoreCase));

        if (conflict != null)
        {
            var msg = $"The shortcut {gesture} is already assigned to view \"{conflict.Name}\".\n\n" +
                      "Reassign it to this view? The other view will lose its shortcut.";
            if (MessageBox.Show(msg, "Shortcut Conflict",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            // Clear the conflicting view's hotkey.
            conflict.Hotkey = null;
        }

        _vm.ApplyHotkey(key, modifiers);
    }

    // ── Close ─────────────────────────────────────────────────────────────────────

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
