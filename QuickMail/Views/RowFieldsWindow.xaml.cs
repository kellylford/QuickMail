using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
            AccessibilityHelper.Announce(this,
                "Use Alt+Up and Alt+Down to reorder. Space turns a field on or off.",
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

    private void FieldList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // The check box inside each row is deliberately not focusable, so the row itself is the
        // single arrow stop; Space has to do the toggling that the check box would.
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None)
        {
            ToggleSelectedField();
            e.Handled = true;
            return;
        }

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

    private static void FocusPane(UIElement pane)
    {
        pane.Focus();
        if (pane is ListBox list && list.Items.Count > 0 &&
            list.ItemContainerGenerator.ContainerFromIndex(
                list.SelectedIndex >= 0 ? list.SelectedIndex : 0) is IInputElement item)
        {
            item.Focus();
            return;
        }
        if (pane is ComboBox or TextBox) return;   // focusable in their own right
        pane.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    /// <summary>
    /// After a move the container the user was on has been recycled to a new index; re-focus the
    /// selected row so keyboard focus follows the field rather than staying at the old position.
    /// </summary>
    private void FocusSelectedFieldContainer() =>
        Dispatcher.InvokeAsync(() =>
        {
            if (FieldList.SelectedIndex < 0) return;
            if (FieldList.ItemContainerGenerator.ContainerFromIndex(FieldList.SelectedIndex)
                is IInputElement container)
                container.Focus();
        }, System.Windows.Threading.DispatcherPriority.Input);

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
