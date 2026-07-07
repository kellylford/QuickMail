using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using QuickMail.Resources;
using QuickMail.ViewModels;

namespace QuickMail.Views;

public partial class ViewManagerWindow : Window
{
    private readonly ViewManagerViewModel _vm;

    private void DayLimitBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Block any keystroke that isn't a digit.
        if (!e.Text.All(char.IsDigit))
            e.Handled = true;
    }

    private void DayLimitBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(typeof(string)))
        {
            var pasted = (string)e.DataObject.GetData(typeof(string))!;
            if (!pasted.All(char.IsDigit))
                e.CancelCommand();
        }
        else
        {
            e.CancelCommand();
        }
    }

    /// <summary>True when opened via Save View… (create-new flow) rather than Manage Views…</summary>
    private readonly bool _createMode;

    /// <summary>Set to true after a successful Save so that OnClosing knows not to delete.</summary>
    private bool _savedInCreateMode;

    public ViewManagerWindow(ViewManagerViewModel vm, bool createMode = false)
    {
        _vm         = vm;
        _createMode = createMode;
        InitializeComponent();
        DataContext = vm;

        DataObject.AddPastingHandler(DayLimitBox, DayLimitBox_Pasting);

        vm.FolderConflictDetected += OnFolderConflict;
        vm.SetHotkeyRequested     += OnSetHotkeyRequested;
        vm.EditModeEntered        += OnEditModeEntered;
        vm.CloseRequested         += (_, _) => Close();

        if (createMode)
            Loaded += OnLoadedCreateMode;
        else
            Loaded += OnLoadedManageMode;
    }

    // ── Manage-mode setup ─────────────────────────────────────────────────────────

    private void OnLoadedManageMode(object sender, RoutedEventArgs e)
    {
        // Land focus in the views list so the user can arrow-key through saved views immediately.
        ViewsList.Focus();
    }

    // ── Create-mode setup ─────────────────────────────────────────────────────────

    private void OnLoadedCreateMode(object sender, RoutedEventArgs e)
    {
        // Retitle the window.
        Title = Strings.ViewManager_SaveViewWindowTitle;

        // Collapse the saved-views list and splitter so the editing panel fills the dialog.
        ListColumn.Width     = new System.Windows.GridLength(0);
        SplitterColumn.Width = new System.Windows.GridLength(0);

        // Swap the bottom button: show "Save View", change "Close" to "Cancel".
        SaveViewAndCloseButton.Visibility = Visibility.Visible;
        CloseButton.Content = Strings.ViewManager_CancelButton;

        // Create a new view from the current app state and select it.
        // SaveAsNew() fires EditModeEntered, which schedules the NameBox focus via
        // OnEditModeEntered at DispatcherPriority.Input — after the visibility update settles.
        _vm.SaveAsNewCommand.Execute(null);
    }

    // ── Edit-mode focus ───────────────────────────────────────────────────────────

    /// <summary>
    /// When the VM enters edit mode, land focus on the most useful field for the context:
    /// <list type="bullet">
    ///   <item><description>
    ///     <b>Create mode</b> (Save View…): focus the Set Hotkey button — the first tab stop —
    ///     so the user walks through shortcut → day limit → name → Save View in order.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Manage mode</b> (Edit existing view): focus the Name field with the text selected
    ///     so a quick rename is one keystroke away.
    ///   </description></item>
    /// </list>
    /// Uses DispatcherPriority.Input so the layout pass that makes the panel visible
    /// (Render priority) runs first.
    /// </summary>
    private void OnEditModeEntered(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_createMode)
            {
                SetHotkeyButton.Focus();
            }
            else
            {
                NameBox.Focus();
                NameBox.SelectAll();
            }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    // ── Save View button (create mode only) ───────────────────────────────────────

    private void SaveViewAndCloseButton_Click(object sender, RoutedEventArgs e)
    {
        _vm.SaveCommand.Execute(null);
        _savedInCreateMode = true;
        Close();
    }

    /// <summary>
    /// If the user dismisses the dialog in create mode without saving (Escape, ✕, Cancel),
    /// remove the view that was auto-created so we don't leave a half-named phantom entry.
    /// Uses <see cref="ViewManagerViewModel.CancelCreate"/> rather than DeleteCommand so
    /// no ViewsChanged event fires mid-close — that would trigger re-entrant UI updates
    /// on the parent window (menu rebuild, folder-tree sync) while the dialog's message
    /// loop is still unwinding, producing a COM apartment violation and crashing the app.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_createMode && !_savedInCreateMode && _vm.SelectedView != null)
            _vm.CancelCreate();
    }

    // ── Folder conflict prompt ────────────────────────────────────────────────────

    private void OnFolderConflict(object? sender, FolderConflictEventArgs e)
    {
        var existing = string.Join(", ", e.ExistingFolders);
        var msg = string.Format(Strings.ViewManager_Announce_FolderConflictMessage, e.NewFolder, existing);

        var result = MessageBox.Show(msg, Strings.ViewManager_FolderChangedTitle,
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
        var dlg = new KeyCaptureDialog
        {
            Owner = this,
            ConflictChecker = (key, modifiers) =>
            {
                var gesture = Helpers.GestureHelper.Format(key, modifiers);
                var conflict = _vm.SavedViews.FirstOrDefault(v =>
                    v != _vm.SelectedView &&
                    !string.IsNullOrEmpty(v.Hotkey) &&
                    string.Equals(v.Hotkey, gesture, StringComparison.OrdinalIgnoreCase));
                return conflict != null ? $"view \"{conflict.Name}\"" : null;
            },
        };

        if (dlg.ShowDialog() != true) return;

        var key       = dlg.CapturedKey;
        var modifiers = dlg.CapturedModifiers;
        var gesture   = Helpers.GestureHelper.Format(key, modifiers);

        // If the user accepted despite a conflict, clear the conflicting view's hotkey first.
        if (dlg.HasConflict)
        {
            var conflict = _vm.SavedViews.FirstOrDefault(v =>
                v != _vm.SelectedView &&
                !string.IsNullOrEmpty(v.Hotkey) &&
                string.Equals(v.Hotkey, gesture, StringComparison.OrdinalIgnoreCase));
            if (conflict != null)
                conflict.Hotkey = null;
        }

        _vm.ApplyHotkey(key, modifiers);
    }

    // ── Close ─────────────────────────────────────────────────────────────────────

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
