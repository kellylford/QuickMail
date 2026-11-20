using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// The Import Settings preview (issue #507). Nothing is written until the user chooses Import and
/// Restart, which stages the file and restarts QuickMail to apply it (spec Revision 2 R1).
/// Modeless, for the same reason as <see cref="ExportSettingsWindow"/>.
/// </summary>
public partial class ImportSettingsWindow : Window
{
    private readonly ImportSettingsViewModel _vm;
    private readonly Action _restart;
    private readonly Func<string?> _whyNotRestart;
    private readonly CommandRegistry _localRegistry = new();
    private IInputElement? _restoreFocusTo;

    /// <param name="restart">Restarts QuickMail once the file is staged; the owner decides how.</param>
    /// <param name="whyNotRestart">Why QuickMail cannot restart now (a message being written), or null.</param>
    public ImportSettingsWindow(ImportSettingsViewModel vm, Action restart, Func<string?> whyNotRestart)
    {
        InitializeComponent();
        _vm = vm;
        _restart = restart;
        _whyNotRestart = whyNotRestart;
        DataContext = vm;
        PreviewKeyDown += OnPreviewKeyDown;
        RegisterLocalCommands();
        Loaded += (_, _) => SummaryBox.Focus();
    }

    public void SetFocusRestoreTarget(IInputElement? element) => _restoreFocusTo = element;

    /// <summary>True once Import and Restart staged the file: the window closed to restart, not to back out.</summary>
    public bool ImportStarted { get; private set; }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        // Checked before anything is staged, so declining leaves nothing waiting for the next start.
        if (_whyNotRestart() is { } reason)
        {
            MessageBox.Show(this, reason, "Import Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            _vm.Stage();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Log("Import settings: staging", ex);
            MessageBox.Show(this, $"The settings file could not be copied into your QuickMail data folder. {ex.Message}",
                            "Import Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ImportStarted = true;
        _restoreFocusTo = null;   // the app is about to restart; there is nothing to return to
        Close();
        _restart();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
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
        _localRegistry.Register(new CommandDefinition(id: "importSettings.import", category: "Settings",
            title: "Import and Restart", execute: () => Import_Click(this, new RoutedEventArgs())));
        _localRegistry.Register(new CommandDefinition(id: "importSettings.close", category: "Settings",
            title: "Close Window", execute: Close));
    }

    // F6 ring: summary → categories → accounts (when there are any) → buttons.
    private void CyclePanes(bool forward)
    {
        var panes = _vm.HasAccounts
            ? new UIElement[] { SummaryBox, CategoryList, AccountList, ImportButton }
            : new UIElement[] { SummaryBox, CategoryList, ImportButton };
        var current = Array.FindIndex(panes, p => p.IsKeyboardFocusWithin || (p == ImportButton && ButtonRow.IsKeyboardFocusWithin));
        var next = current < 0 ? 0 : forward ? (current + 1) % panes.Length : (current - 1 + panes.Length) % panes.Length;
        if (panes[next] is ListBox list) FocusListItem(list);
        else panes[next].Focus();
    }

    // The selected item, not the list box: a focused list with no item focused announces the list and nothing in it.
    private static void FocusListItem(ListBox list)
    {
        if (list.Items.Count == 0) { list.Focus(); return; }
        if (list.SelectedIndex < 0) list.SelectedIndex = 0;
        list.UpdateLayout();
        if (list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex) is UIElement item) item.Focus();
        else list.Focus();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        try { _restoreFocusTo?.Focus(); } catch (InvalidOperationException) { /* the target may be gone */ }
    }
}
