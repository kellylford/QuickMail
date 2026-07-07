using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

[SuppressMessage("Design", "CA1001", Justification = "Window cleans up _loadCts on close; WPF never calls Dispose on a Window, so implementing IDisposable would be dead code.")]
public partial class FlagManagerWindow : Window
{
    private readonly FlagManagerViewModel _vm;
    private readonly CommandRegistry _localRegistry = new();
    private CancellationTokenSource? _loadCts;

    public FlagManagerWindow(FlagManagerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;

        RegisterLocalCommands();

        vm.ConfirmDeleteRequested += OnConfirmDelete;
        vm.AnnouncementRequested  += OnAnnouncement;
        vm.RenameStarted          += OnRenameStarted;

        Loaded += async (_, _) =>
        {
            _loadCts = new CancellationTokenSource();
            try { await _vm.LoadAsync(_loadCts.Token); }
            catch (OperationCanceledException) { return; }
            FocusPane(FlagList);
        };
    }

    private async Task<bool> OnConfirmDelete(string msg, string title) =>
        await Application.Current.Dispatcher.InvokeAsync(() =>
            MessageBox.Show(this, msg, title, MessageBoxButton.YesNo, MessageBoxImage.Warning)
            == MessageBoxResult.Yes);

    private void OnAnnouncement(string text, AnnouncementCategory cat) =>
        AccessibilityHelper.Announce(this, text, interrupt: true, category: cat);

    private void OnRenameStarted(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(() => EditNameBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);

    private void RegisterLocalCommands()
    {
        _localRegistry.Register(new CommandDefinition(
            "flagmgr.add", "Flag", "Add Flag",
            () => _vm.AddFlagCommand.Execute(null)));
        _localRegistry.Register(new CommandDefinition(
            "flagmgr.delete", "Flag", "Delete Selected Flag",
            () => _vm.DeleteFlagCommand.Execute(null),
            isAvailable: () => _vm.CanDelete));
        _localRegistry.Register(new CommandDefinition(
            "flagmgr.rename", "Flag", "Rename Selected Flag",
            () => _vm.BeginRenameCommand.Execute(null),
            isAvailable: () => _vm.HasSelection));
        _localRegistry.Register(new CommandDefinition(
            "flagmgr.moveup", "Flag", "Move Flag Up",
            () => _vm.MoveUpCommand.Execute(null),
            isAvailable: () => _vm.CanMoveUp));
        _localRegistry.Register(new CommandDefinition(
            "flagmgr.movedown", "Flag", "Move Flag Down",
            () => _vm.MoveDownCommand.Execute(null),
            isAvailable: () => _vm.CanMoveDown));
        _localRegistry.Register(new CommandDefinition(
            "flagmgr.setdefault", "Flag", "Set as K Key Default",
            () => _vm.SetAsKDefaultCommand.Execute(null),
            isAvailable: () => _vm.HasSelection));
        _localRegistry.Register(new CommandDefinition(
            "flagmgr.close", "Flag", "Close",
            () => Close()));
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Key == Key.F6)
        {
            CycleFocus(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
            return;
        }

        if (e.Key == Key.P &&
            Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            OpenCommandPalette();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && FlagList.IsKeyboardFocusWithin && !_vm.IsRenaming)
        {
            _vm.BeginRenameCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && EditNameBox.IsKeyboardFocusWithin)
        {
            _vm.SaveRenameCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _vm.IsRenaming)
        {
            _vm.CancelRenameCommand.Execute(null);
            FlagList.Focus();
            e.Handled = true;
        }
    }

    private void CycleFocus(bool reverse)
    {
        UIElement[] panes = [FlagToolbar, FlagList, EditPanel];
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
        if (pane is System.Windows.Controls.ToolBar tb)
        {
            tb.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            return;
        }
        if (pane is System.Windows.Controls.Primitives.Selector sel &&
            sel.Items.Count > 0 &&
            sel.ItemContainerGenerator.ContainerFromIndex(
                sel.SelectedIndex >= 0 ? sel.SelectedIndex : 0) is IInputElement item)
        {
            item.Focus();
            return;
        }
        pane.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void OpenCommandPalette()
    {
        var prev = Keyboard.FocusedElement as IInputElement;
        var palette = new CommandPaletteWindow(_localRegistry) { Owner = this };
        palette.ShowDialog();
        (prev ?? FlagList).Focus();
    }

    protected override void OnClosed(EventArgs e)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _vm.ConfirmDeleteRequested -= OnConfirmDelete;
        _vm.AnnouncementRequested  -= OnAnnouncement;
        _vm.RenameStarted          -= OnRenameStarted;
        base.OnClosed(e);
    }
}
