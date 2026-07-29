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
        _vm.FieldFocusRequested += FocusField;

        RegisterPaletteCommands();

        Loaded += (_, _) =>
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
            AccessibilityHelper.Announce(this,
                (vm.IsRecurringEdit
                    ? "This is a repeating event. Choose what to change: this event only, or all events " +
                      "in the series. "
                    : string.Empty) +
                "Tab through the fields. In a date or time field, use the up and down arrows to change " +
                "the value, or type one. Press Control plus Enter to save, Escape to cancel.",
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

        // Escape while a ComboBox dropdown is open: let the control consume it (the
        // modeless-dialog Escape guard in CLAUDE.md). The two DatePickers that used to be listed
        // here are gone — their date entry is now a plain editable field with no popup at all.
        if (e.Key == Key.Escape
            && (RepeatCombo.IsDropDownOpen || SaveTargetCombo.IsDropDownOpen))
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

    /// <summary>
    /// Puts focus on the field a refused save blamed, and selects its text so a correction can be
    /// typed straight over it. Focus movement is announced by the screen reader as ordinary
    /// navigation, which is why it still tells the user something when every QuickMail
    /// announcement setting is switched off.
    /// </summary>
    private void FocusField(EditorField field)
    {
        Control? target = field switch
        {
            EditorField.Title => TitleBox,
            EditorField.Start => StartDateField,
            EditorField.End => EndDateField,
            EditorField.Repeat => RepeatCombo,
            EditorField.RepeatInterval => RepeatIntervalField,
            EditorField.RepeatUntil => RepeatUntilField,
            EditorField.SaveTarget => SaveTargetCombo,
            _ => null,
        };
        if (target is null) return;

        target.Focus();
        if (target is TextBox box) box.SelectAll();
    }

    /// <summary>Cycles focus across the editor's logical stops: Title → Start → Notes → Save.</summary>
    private void CycleFocus(bool forward)
    {
        Control[] stops = { TitleBox, StartDateField, NotesBox, SaveButton };
        var current = System.Array.FindIndex(stops, c => c.IsKeyboardFocusWithin);
        if (current < 0) current = 0;
        var next = forward
            ? (current + 1) % stops.Length
            : (current - 1 + stops.Length) % stops.Length;
        stops[next].Focus();
    }
}
