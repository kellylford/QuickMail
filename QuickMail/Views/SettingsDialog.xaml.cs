using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

public partial class SettingsDialog : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsDialog(SettingsViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        vm.DiagnosticsAnnouncementRequested += OnDiagnosticsAnnouncement;
        vm.SoundResultAnnounced += OnSoundResult;
        vm.ConfirmRequested ??= Confirm;
        Closing += (_, e) => _closing = !e.Cancel;
        Closed += (_, _) =>
        {
            _closing = true;
            vm.DiagnosticsAnnouncementRequested -= OnDiagnosticsAnnouncement;
            vm.SoundResultAnnounced -= OnSoundResult;
            _schemeNameDialog?.Close();
            vm.StopSounds();
        };
    }

    private SoundSchemeNameDialog? _schemeNameDialog;
    private bool _closing;

    private void OnSoundResult(string text) =>
        AccessibilityHelper.Announce(this, text, interrupt: true, AnnouncementCategory.Result);

    // A message box has no editable text, so a modal one is safe here (CLAUDE.md, Modal Dialog Rules).
    private bool Confirm(string question, string title) =>
        MessageBox.Show(this, question, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void ChooseSoundFileButton_Click(object sender, RoutedEventArgs e)
    {
        // On success, Sound for this event, so its value, the new file, is read (#932). Otherwise back
        // here: the file dialog gives focus back on its own only some of the time.
        if (_vm.TryChooseSoundFile()) SoundChoiceCombo.Focus();
        else ChooseSoundFileButton.Focus();
    }

    // ── Sound schemes (#932) ─────────────────────────────────────────────────────

    // The name dialog is modeless, so Settings can change underneath it: each action is tied to the
    // scheme showing when it was asked for, and reports a problem unless it really happened.
    private void NewSoundSchemeButton_Click(object sender, RoutedEventArgs e) =>
        AskSchemeName("New Sound Scheme", string.Empty,
            name => _vm.CreateScheme(name) ? null : _vm.SchemeNameProblem(name) ?? "The sound scheme could not be made.",
            () => SoundEventList);

    private void CopySoundSchemeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedSoundScheme is not { } source) return;
        AskSchemeName("Copy Sound Scheme", $"{source.Name} copy",
            name => _vm.CreateScheme(name, source.Id) ? null
                : _vm.SchemeNameProblem(name) ?? $"The sound scheme {source.Name} is no longer there to copy.",
            () => SoundEventList);
    }

    private void RenameSoundSchemeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedSoundScheme is not { IsBuiltIn: false } scheme) return;
        AskSchemeName("Rename Sound Scheme", scheme.Name,
            name => _vm.RenameScheme(scheme.Id, name) ? null : _vm.SchemeRenameProblem(scheme.Id, name),
            () => SoundSchemeCombo);
    }

    private void DeleteSoundSchemeButton_Click(object sender, RoutedEventArgs e)
    {
        // Rename and Delete are disabled now, so focus can't stay on this button.
        if (_vm.DeleteSelectedScheme()) SoundSchemeCombo.Focus();
    }

    private void ResetSoundSchemeButton_Click(object sender, RoutedEventArgs e)
    {
        // Reset is disabled now; the selected event reads its sound as shipped.
        if (_vm.TryResetScheme()) FocusSelectedEvent();
    }

    private void RemoveSoundFileButton_Click(object sender, RoutedEventArgs e)
    {
        // Remove file is disabled now; Sound for this event reads the file as missing.
        if (_vm.TryRemoveSoundFile()) SoundChoiceCombo.Focus();
        else RemoveSoundFileButton.Focus();
    }

    private void ExportSoundSchemeButton_Click(object sender, RoutedEventArgs e)
    {
        _vm.ExportSchemeCommand.Execute(null);
        ExportSoundSchemeButton.Focus();   // the file dialog gives focus back on its own only some of the time
    }

    // The gallery (#937). Modal over Settings: it has no editable text, so the nested loop is safe.
    private void BrowseSoundSchemesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SoundGallery is not { } gallery) return;
        // The schemes here, saved or not, so the gallery can say which it has and which have updates.
        var window = new SoundGalleryWindow(new SoundGalleryViewModel(gallery, _vm.GalleryOrigins())) { Owner = this };
        if (window.ShowDialog() != true || window.DownloadedPath is not { } path)
        {
            BrowseSoundSchemesButton.Focus();
            return;
        }
        try
        {
            if (_vm.ImportSchemeFrom(path, window.DownloadedEntry)) FocusSelectedEvent();
            else BrowseSoundSchemesButton.Focus();
        }
        finally
        {
            SoundGalleryService.DeleteDownload(path);
        }
    }

    private void ImportSoundSchemeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.TryImportScheme()) FocusSelectedEvent();
        else ImportSoundSchemeButton.Focus();
    }

    /// <summary>Focuses the selected event's row, not the list, so the row is read.</summary>
    private void FocusSelectedEvent()
    {
        if (_vm.SelectedSoundEvent is not { } row)
        {
            SoundEventList.Focus();
            return;
        }
        SoundEventList.ScrollIntoView(row);
        SoundEventList.UpdateLayout();
        if (SoundEventList.ItemContainerGenerator.ContainerFromItem(row) is UIElement item) item.Focus();
        else SoundEventList.Focus();
    }

    /// <summary>
    /// Asks for a name in a modeless dialog, then puts focus where the person goes next. A list's item,
    /// not the list, takes focus, so the row is read.
    /// </summary>
    private void AskSchemeName(string title, string initialName, Func<string, string?> accept, Func<IInputElement> focusAfter)
    {
        if (_schemeNameDialog is not null)
        {
            _schemeNameDialog.Activate();
            return;
        }

        var returnTo = Keyboard.FocusedElement;
        var dialog = new SoundSchemeNameDialog(title, initialName, accept) { Owner = this };
        _schemeNameDialog = dialog;
        dialog.Closed += (_, _) =>
        {
            _schemeNameDialog = null;
            // Closed along with Settings: there is nothing to come back to, and Activate would throw.
            if (_closing) return;
            Activate();
            Dispatcher.InvokeAsync(() =>
            {
                if (_closing) return;
                if (!dialog.Accepted)
                {
                    returnTo?.Focus();
                    return;
                }
                var target = focusAfter();
                if (target == SoundEventList) FocusSelectedEvent();
                else target.Focus();
            }, DispatcherPriority.Input);
        };
        dialog.Show();
    }

    // Meta-announcement about screenshot capture (#175): force bypasses the
    // user's announcement preferences, matching the custom-announcements toggle.
    private void OnDiagnosticsAnnouncement(string text) =>
        AccessibilityHelper.Announce(this, text, interrupt: false, AnnouncementCategory.Status, force: true);

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Defer focus until after the window is activated so the screen reader picks it up.
        Dispatcher.InvokeAsync(() => PreviewLinesCombo.Focus(), DispatcherPriority.Input);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _vm.SaveCommand.Execute(null);
        LogService.Format  = _vm.LogFormat;
        LogService.Enabled = _vm.EnableLogging;

        // Applied live rather than at next startup: someone turning this on is troubleshooting a
        // problem that is happening now, and making them restart would lose the very thing they
        // are trying to capture.
        ConnectionJournal.Enabled = _vm.ConnectionDiagnostics;
        // Live too, so the fix can be switched off and on while testing without a restart (#300).
        TypedSpaceFix.SetEnabled(_vm.NvdaTypingFix);
        DialogResult = true;
        Close();
    }

    private void DeleteLogButton_Click(object sender, RoutedEventArgs e)
    {
        // Deletes BOTH logs and all debug screenshots. Someone deleting their logs means all of
        // them — most often because the artifacts hold their email addresses, server names, or
        // (for screenshots, #436) pictures of their actual mail, and they don't want them on
        // disk. Leaving any diagnostic file behind because it happens to live elsewhere would
        // quietly defeat that, and nothing in the UI would hint that it still existed.
        var result = MessageBox.Show(
            "Delete QuickMail's log files? This deletes the application log, the connection " +
            "diagnostics log, and any saved debug screenshots, and cannot be undone.",
            "Delete Logs",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (result == MessageBoxResult.Yes)
        {
            LogService.DeleteLog();
            ConnectionJournal.DeleteLog();
            // Static + profile-keyed: works in normal launches too, where only the
            // null capture service is wired but old /debug screenshots may remain.
            // Flush first so no in-flight save holds a handle open against the
            // delete or resurrects a capture after it.
            var app = Application.Current as App;
            (app?.ScreenshotCapture as ScreenshotCaptureService)?.FlushPendingSaves(TimeSpan.FromSeconds(3));
            if (app?.Profile is { } profile)
                ScreenshotCaptureService.DeleteAllCaptures(profile.ProfileDir);
        }

        DeleteLogButton.Focus();
    }

    private void HotkeyListView_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateActionButtons();
    }

    // First-letter navigation in the shortcuts list (#852), the same hand-rolled type-ahead as the
    // folder tree: W moves to the next command whose name starts with W, W again a moment later to
    // the one after, and letters typed quickly match more of the name.
    //
    // Every typed character is taken here and marked handled, matched or not, capitals and AltGr
    // characters included. Text that reaches the window fires the dialog's access keys without Alt:
    // Shift+S would run Save, Shift+C Cancel, G switch to the General tab, as "c" once closed the
    // folder picker (#418). WPF's own TextSearch is off on this list; it lets unmatched text through.
    // Alt+letter arrives as system text, not here, so the access keys still work with Alt.
    internal TypeAheadPrefixTracker HotkeyTypeAhead { get; set; } = new();

    private void HotkeyListView_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Text) || e.Text.Any(char.IsControl)) return;
        e.Handled = true;
        TryHotkeyTypeAhead(e.Text);
    }

    /// <summary>Moves the shortcuts list to the next command matching the typed text; false if none does.</summary>
    internal bool TryHotkeyTypeAhead(string text)
    {
        // Recorded matched or not, like the other lists: "r", "x", "e" searches for "rxe".
        if (!HotkeyTypeAhead.TryAppend(text, HotkeyListView, out var prefix)) return false;

        var rows = _vm.HotkeyRows;
        var current = _vm.SelectedHotkey is { } selected ? rows.IndexOf(selected) : -1;
        // A longer prefix ("wa" after "w") checks the current command first, so it stays put while
        // its name still matches; a single letter, new or repeated, moves on to the next one.
        var start = prefix.Length > 1 && current >= 0 ? current - 1 : current;
        var index = TypeAheadMatcher.FindNext(rows, r => r.Title, start, prefix);
        if (index < 0) return false;

        var row = rows[index];
        _vm.SelectedHotkey = row;
        HotkeyListView.ScrollIntoView(row);
        HotkeyListView.UpdateLayout();
        (HotkeyListView.ItemContainerGenerator.ContainerFromItem(row) as UIElement)?.Focus();
        return true;
    }

    // The same type-ahead for the sound events, for the same reason: WPF's TextSearch lets unmatched
    // text through, and in this dialog that would fire an access key without Alt (#418).
    internal TypeAheadPrefixTracker SoundEventTypeAhead { get; set; } = new();

    private void SoundEventList_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Text) || e.Text.Any(char.IsControl)) return;
        e.Handled = true;
        TrySoundEventTypeAhead(e.Text);
    }

    /// <summary>Moves the sound events list to the next event matching the typed text; false if none does.</summary>
    internal bool TrySoundEventTypeAhead(string text)
    {
        if (!SoundEventTypeAhead.TryAppend(text, SoundEventList, out var prefix)) return false;

        var rows = _vm.SoundEventRows;
        var current = _vm.SelectedSoundEvent is { } selected ? rows.IndexOf(selected) : -1;
        var start = prefix.Length > 1 && current >= 0 ? current - 1 : current;
        var index = TypeAheadMatcher.FindNext(rows, r => r.Label, start, prefix);
        if (index < 0) return false;

        var row = rows[index];
        _vm.SelectedSoundEvent = row;
        SoundEventList.ScrollIntoView(row);
        SoundEventList.UpdateLayout();
        (SoundEventList.ItemContainerGenerator.ContainerFromItem(row) as UIElement)?.Focus();
        return true;
    }

    private void HotkeyListView_KeyDown(object sender, KeyEventArgs e)
    {
        if (_vm.SelectedHotkey == null) return;

        if (e.Key == Key.Return)
        {
            OpenSetShortcutDialog(_vm.SelectedHotkey);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _vm.SelectedHotkey.HasCustomBinding)
        {
            _vm.SelectedHotkey.ClearCustomBinding();
            UpdateActionButtons();
            e.Handled = true;
        }
    }

    private void SetShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedHotkey != null)
            OpenSetShortcutDialog(_vm.SelectedHotkey);
    }

    private void OpenSetShortcutDialog(SettingsViewModel.HotkeyRowViewModel row)
    {
        LogService.Debug($"[Settings] Opening capture dialog for '{row.CommandId}'");

        var captureDialog = new KeyCaptureDialog
        {
            Owner = this,
            ConflictChecker = (key, modifiers) =>
            {
                var conflict = _vm.FindConflict(key, modifiers);
                return (conflict != null && conflict != row) ? conflict.Title : null;
            },
        };

        if (captureDialog.ShowDialog() != true)
        {
            LogService.Debug("[Settings] Key capture cancelled");
            return;
        }

        var key       = captureDialog.CapturedKey;
        var modifiers = captureDialog.CapturedModifiers;
        var gesture   = GestureHelper.Format(key, modifiers);
        LogService.Debug($"[Settings] Captured: key={key} modifiers={modifiers} gesture='{gesture}'");

        // If the user accepted despite a conflict, clear the conflicting binding first.
        if (captureDialog.HasConflict)
        {
            var conflict = _vm.FindConflict(key, modifiers);
            if (conflict != null && conflict != row)
            {
                LogService.Debug($"[Settings] Reassigning from '{conflict.CommandId}'");
                conflict.ClearCustomBinding();
            }
        }

        row.SetCustomBinding(key, modifiers);
        UpdateActionButtons();
        LogService.Debug($"[Settings] Assigned '{gesture}' to '{row.Title}' — announcing");
        AnnounceAssignment(gesture, row.Title);
    }

    private void AnnounceAssignment(string gesture, string commandTitle)
    {
        var source = Keyboard.FocusedElement as UIElement ?? this;
        var text   = $"{gesture} assigned to {commandTitle}";

        LogService.Debug($"[Settings] Announce: source={source.GetType().Name} text='{text}'");

        try
        {
            AccessibilityHelper.Announce(source, text, interrupt: true);
            LogService.Debug("[Settings] Announce returned OK");
        }
        catch (Exception ex)
        {
            LogService.Log("[Settings] Announce failed", ex);
        }
    }

    // There is deliberately no Checked handler for the settings radio buttons. Speaking the
    // option ourselves duplicates what the platform already reports and overrides the behaviour
    // the user has configured in their own software — a defect, not a feature. One was added in
    // f71f86f to compensate for choices that were not being announced, but the real fault was
    // that arrowing moved focus without selecting anything (issue #441); with selection
    // following focus, the ordinary focus and state reporting covers it.

    private void RestoreDefaultButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedHotkey == null) return;
        _vm.SelectedHotkey.ClearCustomBinding();
        UpdateActionButtons();
    }

    private void UpdateActionButtons()
    {
        var row = _vm.SelectedHotkey;
        SetShortcutButton.IsEnabled    = row != null;
        RestoreDefaultButton.IsEnabled = row?.HasCustomBinding == true;
    }
}
