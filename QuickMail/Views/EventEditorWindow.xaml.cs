using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// Modeless editor for a locally-created calendar appointment. Modeless per the CLAUDE.md modal
/// rules — an editable-text dialog opened over the main window's live WebView2 reading pane must
/// not use <c>ShowDialog()</c>. Escape / Cancel / Ctrl+Enter and the command palette are wired
/// explicitly because a modeless window has no <c>DialogResult</c>.
/// </summary>
public partial class EventEditorWindow : Window
{
    private readonly EventEditorViewModel _vm;
    private readonly CommandRegistry _registry = new();

    public EventEditorWindow(EventEditorViewModel vm)
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
            TitleBox.Focus();
            TitleBox.SelectAll();
            AccessibilityHelper.Announce(this,
                vm.IsRecurringEdit
                    ? "This is a repeating event. Choose what to change: this event only, or all events " +
                      "in the series. Tab through the fields. Press Control plus Enter to save, Escape to cancel."
                    : "Tab through the fields. Press Control plus Enter to save, Escape to cancel.",
                category: AnnouncementCategory.Hint);
        };
    }

    private void RegisterPaletteCommands()
    {
        _registry.Register(new CommandDefinition(
            id: "editor.save", category: "Calendar", title: "Save appointment",
            execute: () => _vm.SaveCommand.Execute(null),
            defaultKey: Key.Enter, defaultModifiers: ModifierKeys.Control));
        _registry.Register(new CommandDefinition(
            id: "editor.cancel", category: "Calendar", title: "Cancel",
            execute: () => _vm.CancelCommand.Execute(null),
            defaultKey: Key.Escape, defaultModifiers: ModifierKeys.None));
    }

    private void Save_Click(object sender, RoutedEventArgs e) => _vm.SaveCommand.Execute(null);
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

        // F6 / Shift+F6 — cycle the logical field groups (framework-level, like the palette).
        if (e.Key == Key.F6)
        {
            e.Handled = true;
            CycleFocus(forward: Keyboard.Modifiers != ModifierKeys.Shift);
            return;
        }

        // Escape while a DatePicker calendar or ComboBox dropdown is open: let the control
        // consume it (the modeless-dialog Escape guard in CLAUDE.md).
        if (e.Key == Key.Escape
            && (StartDatePicker.IsDropDownOpen || EndDatePicker.IsDropDownOpen
                || RepeatCombo.IsDropDownOpen || SaveTargetCombo.IsDropDownOpen))
            return;

        // Registry dispatch (ComposeWindow pattern) — so editor.save / editor.cancel rebindings
        // in the keyboard customizations dialog actually take effect.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var cmd = _registry.FindByGesture(key, Keyboard.Modifiers);
        if (cmd != null && (cmd.IsAvailable?.Invoke() ?? true))
        {
            e.Handled = true;
            cmd.Execute();
        }
    }

    /// <summary>Cycles focus across the editor's logical stops: Title → Start → Notes → Save.</summary>
    private void CycleFocus(bool forward)
    {
        Control[] stops = { TitleBox, StartDatePicker, NotesBox, SaveButton };
        var current = System.Array.FindIndex(stops, c => c.IsKeyboardFocusWithin);
        if (current < 0) current = 0;
        var next = forward
            ? (current + 1) % stops.Length
            : (current - 1 + stops.Length) % stops.Length;
        stops[next].Focus();
    }
}
