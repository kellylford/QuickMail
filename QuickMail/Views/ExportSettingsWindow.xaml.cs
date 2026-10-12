using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// Export Settings (issue #507). Modeless: it is opened over the main window, which hosts the live
/// WebView2 reading pane, and a modal dialog there can deadlock the UI thread with a screen reader
/// running (CLAUDE.md modal dialog rules). Escape and Cancel are therefore wired explicitly.
/// </summary>
public partial class ExportSettingsWindow : Window
{
    private readonly ExportSettingsViewModel _vm;
    private readonly CommandRegistry _localRegistry = new();
    private IInputElement? _restoreFocusTo;

    public ExportSettingsWindow(ExportSettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        PreviewKeyDown += OnPreviewKeyDown;
        RegisterLocalCommands();
        Loaded += (_, _) => CategoryList.FocusRow(0);
    }

    /// <summary>Where focus returns when the window closes (WPF's return-to-owner is unreliable for list items).</summary>
    public void SetFocusRestoreTarget(IInputElement? element) => _restoreFocusTo = element;

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.CanExportNow) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export Settings",
            FileName = _vm.DefaultFileName,
            DefaultExt = SettingsBundle.FileExtension,
            Filter = $"QuickMail settings (*{SettingsBundle.FileExtension})|*{SettingsBundle.FileExtension}",
            AddExtension = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var message = await _vm.ExportAsync(dialog.FileName);
            // Announced from the owner after closing: a notification raised by a window that is
            // closing can be cut off with it.
            var owner = Owner;
            Close();
            if (owner is not null)
                AccessibilityHelper.Announce(owner, message, category: AnnouncementCategory.Result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LogService.Log("Export settings", ex);
            MessageBox.Show(this, $"The settings could not be exported. {ex.Message}", "Export Settings",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            // Not while a file is being written: closing would hide the outcome the user needs to hear.
            if (!_vm.IsExporting) Close();
        }
        else if (ctrl && shift && e.Key == Key.P)
        {
            e.Handled = true;
            new CommandPaletteWindow(_localRegistry) { Owner = this }.ShowDialog();
        }
        else if (e.Key == Key.F6)
        {
            e.Handled = true;
            CyclePanes(forward: !shift);
        }
    }

    private void RegisterLocalCommands()
    {
        _localRegistry.Register(new CommandDefinition(id: "exportSettings.export", category: "Settings",
            title: "Export…", execute: () => Export_Click(this, new RoutedEventArgs())));
        _localRegistry.Register(new CommandDefinition(id: "exportSettings.close", category: "Settings",
            title: "Close Window", execute: Close));
    }

    // F6 ring: categories → summary → buttons.
    private void CyclePanes(bool forward)
    {
        var current = CategoryList.IsKeyboardFocusWithin ? 0 : SummaryBox.IsKeyboardFocusWithin ? 1 : ButtonRow.IsKeyboardFocusWithin ? 2 : -1;
        var next = forward ? (current + 1) % 3 : (current + 2) % 3;
        switch (next)
        {
            case 0: CategoryList.FocusLastFocusedRow(); break;
            case 1: SummaryBox.Focus(); break;
            default: (ExportButton.IsEnabled ? ExportButton : (UIElement)ButtonRow.Children[1]).Focus(); break;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        try { _restoreFocusTo?.Focus(); } catch (InvalidOperationException) { /* the target may be gone */ }
    }
}
