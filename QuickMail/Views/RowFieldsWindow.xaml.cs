using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// Lets the user choose which fields each kind of message list row speaks, and in what order.
///
/// <para>Modeless (see the modal-dialog rules in CLAUDE.md): it is opened over the main window's
/// live WebView2, so a nested modal message loop is not safe here. That costs the automatic
/// Escape/IsCancel behaviour, which is wired explicitly below.</para>
///
/// <para>There is no OK/Cancel: every change saves immediately, which is also what re-speaks the
/// rows behind this window — the user can leave it open, reorder, and hear the result by arrowing
/// the list.</para>
/// </summary>
public partial class RowFieldsWindow : Window
{
    private readonly RowFieldsViewModel _vm;
    private readonly CommandRegistry _localRegistry = new();

    public RowFieldsWindow(RowFieldsViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;

        RegisterLocalCommands();

        _vm.AnnouncementRequested += OnAnnouncement;

        CloseButton.Click += (_, _) => Close();
        FieldList.PreviewKeyDown += FieldList_PreviewKeyDown;

        Loaded += (_, _) =>
        {
            FocusPane(FieldList);
            // Only the non-obvious gesture. Space on a check box is standard and the platform
            // reports the result, so saying so here would be telling the user what they know.
            AccessibilityHelper.Announce(this,
                "Use Alt+Up and Alt+Down to reorder.",
                interrupt: false, category: AnnouncementCategory.Hint);
        };
    }

    private void OnAnnouncement(string text, AnnouncementCategory category) =>
        AccessibilityHelper.Announce(this, text, interrupt: true, category: category);

    private void RegisterLocalCommands()
    {
        _localRegistry.Register(new CommandDefinition(
            "rowfields.moveup", "View", "Move Field Up",
            () => _vm.MoveUpCommand.Execute(null),
            isAvailable: () => _vm.CanMoveUp));
        _localRegistry.Register(new CommandDefinition(
            "rowfields.movedown", "View", "Move Field Down",
            () => _vm.MoveDownCommand.Execute(null),
            isAvailable: () => _vm.CanMoveDown));
        _localRegistry.Register(new CommandDefinition(
            "rowfields.toggle", "View", "Turn Selected Field On or Off",
            ToggleSelectedField,
            isAvailable: () => _vm.HasSelection));
        _localRegistry.Register(new CommandDefinition(
            "rowfields.reset", "View", "Reset This Row Type to Defaults",
            () => _vm.ResetDefaultsCommand.Execute(null)));
        _localRegistry.Register(new CommandDefinition(
            "rowfields.labels", "View", "Toggle Speak Field Labels",
            () => _vm.ShowFieldLabels = !_vm.ShowFieldLabels));
        _localRegistry.Register(new CommandDefinition(
            "rowfields.close", "View", "Close",
            Close));
    }

    private void ToggleSelectedField()
    {
        if (_vm.SelectedField is { } field) field.Enabled = !field.Enabled;
    }

    /// <summary>
    /// The rows are real check boxes, so keyboard focus lands on the check box rather than on the
    /// ListBoxItem. Selection has to follow it, because Move Up/Down act on the selected field.
    /// </summary>
    private void FieldCheckBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RowFieldRow row })
            _vm.SelectedField = row;
    }

    private void FieldList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Space is deliberately NOT handled here: the check box toggles itself, and the platform
        // reports that state change. Intercepting it would mean re-announcing what is already said.

        // Reorder without leaving the list.
        if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is Key.Up or Key.Down)
        {
            if (e.SystemKey == Key.Up) _vm.MoveUpCommand.Execute(null);
            else                       _vm.MoveDownCommand.Execute(null);
            FocusSelectedFieldContainer();
            e.Handled = true;
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;

        if (e.Key == Key.F6)
        {
            CycleFocus(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
            return;
        }

        if (e.Key == Key.P && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            OpenCommandPalette();
            e.Handled = true;
            return;
        }

        // Modeless windows get no IsCancel handling, so Escape is wired by hand. Guarded so it
        // does not steal Escape from an open ComboBox dropdown.
        if (e.Key == Key.Escape)
        {
            if (RowTypeList.IsDropDownOpen) return;
            Close();
            e.Handled = true;
        }
    }

    private void CycleFocus(bool reverse)
    {
        UIElement[] panes = [RowTypeList, FieldList, OptionsPanel, PreviewBox, ButtonBar];
        int current = -1;
        for (int i = 0; i < panes.Length; i++)
        {
            if (panes[i].IsKeyboardFocusWithin) { current = i; break; }
        }
        int next = reverse
            ? (current <= 0 ? panes.Length - 1 : current - 1)
            : (current >= panes.Length - 1 ? 0 : current + 1);
        FocusPane(panes[next]);
    }

    private void FocusPane(UIElement pane)
    {
        if (ReferenceEquals(pane, FieldList))
        {
            FocusFieldRow(FieldList.SelectedIndex >= 0 ? FieldList.SelectedIndex : 0);
            return;
        }
        pane.Focus();
        if (pane is ComboBox or TextBox) return;   // focusable in their own right
        pane.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    /// <summary>
    /// Focuses a field row's check box. The ListBoxItem containers are Focusable=False so that each
    /// row's real control is the arrow stop, which means focus has to be placed on the check box
    /// inside the container rather than on the container itself.
    /// </summary>
    private void FocusFieldRow(int index)
    {
        if (index < 0 || index >= FieldList.Items.Count) return;
        FieldList.UpdateLayout();
        if (FieldList.ItemContainerGenerator.ContainerFromIndex(index) is not DependencyObject c) return;
        if (FindCheckBox(c) is { } box) box.Focus();
    }

    private static CheckBox? FindCheckBox(DependencyObject root)
    {
        if (root is CheckBox cb) return cb;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindCheckBox(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        }
        return null;
    }

    /// <summary>
    /// After a move the container the user was on has been recycled to a new index; re-focus the
    /// selected row so keyboard focus follows the field rather than staying at the old position.
    /// </summary>
    private void FocusSelectedFieldContainer() =>
        Dispatcher.InvokeAsync(() => FocusFieldRow(FieldList.SelectedIndex),
            System.Windows.Threading.DispatcherPriority.Input);

    private void OpenCommandPalette()
    {
        var prev = Keyboard.FocusedElement as IInputElement;
        var palette = new CommandPaletteWindow(_localRegistry) { Owner = this };
        palette.ShowDialog();
        (prev ?? FieldList).Focus();
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.AnnouncementRequested -= OnAnnouncement;
        FieldList.PreviewKeyDown  -= FieldList_PreviewKeyDown;
        base.OnClosed(e);
    }
}
