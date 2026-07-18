using System.Windows;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// Modeless picker for jumping the calendar to a date. Modeless per the CLAUDE.md modal rules —
/// an editable-field dialog (the DatePicker) opened over the main window's live WebView2 reading
/// pane must not use <c>ShowDialog()</c>. Escape / Cancel / Enter and the command palette are wired
/// explicitly because a modeless window has no <c>DialogResult</c>.
/// </summary>
public partial class GoToDateWindow : Window
{
    private readonly GoToDateViewModel _vm;
    private readonly CommandRegistry _registry = new();

    public GoToDateWindow(GoToDateViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

        _vm.Saved += _ => Close();
        _vm.Cancelled += Close;
        _vm.AnnouncementRequested += (text, category) =>
            AccessibilityHelper.Announce(this, text, category: category);

        RegisterPaletteCommands();

        Loaded += (_, _) =>
        {
            DatePickerBox.Focus();
            AccessibilityHelper.Announce(this,
                "Choose a date, then press Go. Press Escape to cancel.",
                category: AnnouncementCategory.Hint);
        };
    }

    private void RegisterPaletteCommands()
    {
        _registry.Register(new CommandDefinition(
            id: "gotodate.confirm", category: "Calendar", title: "Go to Date",
            execute: () => _vm.ConfirmCommand.Execute(null),
            defaultKey: Key.Enter, defaultModifiers: ModifierKeys.Control));
        _registry.Register(new CommandDefinition(
            id: "gotodate.cancel", category: "Calendar", title: "Cancel",
            execute: () => _vm.CancelCommand.Execute(null),
            defaultKey: Key.Escape, defaultModifiers: ModifierKeys.None));
    }

    private void Go_Click(object sender, RoutedEventArgs e) => _vm.ConfirmCommand.Execute(null);
    private void Cancel_Click(object sender, RoutedEventArgs e) => _vm.CancelCommand.Execute(null);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Shift+P — command palette (framework-level; cannot dispatch through itself).
        if (e.Key == Key.P && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            new CommandPaletteWindow(_registry) { Owner = this }.ShowDialog();
            return;
        }

        // F6 / Shift+F6 — cycle the logical stops (framework-level, like the palette).
        if (e.Key == Key.F6)
        {
            e.Handled = true;
            CycleFocus(forward: Keyboard.Modifiers != ModifierKeys.Shift);
            return;
        }

        // Escape while the DatePicker calendar drop-down is open: let the control consume it
        // (the modeless-dialog Escape guard in CLAUDE.md) rather than closing the window.
        if (e.Key == Key.Escape && DatePickerBox.IsDropDownOpen)
            return;

        // Registry dispatch (ComposeWindow pattern) — so gotodate.confirm / gotodate.cancel
        // rebindings in the keyboard customizations dialog actually take effect.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var cmd = _registry.FindByGesture(key, Keyboard.Modifiers);
        if (cmd != null && (cmd.IsAvailable?.Invoke() ?? true))
        {
            e.Handled = true;
            cmd.Execute();
        }
    }

    /// <summary>Cycles focus across the picker's logical stops: Date → Go.</summary>
    private void CycleFocus(bool forward)
    {
        System.Windows.Controls.Control[] stops = { DatePickerBox, GoButton };
        var current = System.Array.FindIndex(stops, c => c.IsKeyboardFocusWithin);
        if (current < 0) current = 0;
        var next = forward
            ? (current + 1) % stops.Length
            : (current - 1 + stops.Length) % stops.Length;
        stops[next].Focus();
    }
}
