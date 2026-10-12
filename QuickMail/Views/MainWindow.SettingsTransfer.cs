using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// Settings export and import (issue #507): the File menu entries, the three windows, and the
/// restart that applies an import. Every window here is modeless: the main window hosts the live
/// WebView2 reading pane (CLAUDE.md modal dialog rules). One of each at a time: asking again brings
/// the open one forward.
/// </summary>
public partial class MainWindow
{
    private ExportSettingsWindow? _exportSettingsWindow;
    private ImportSettingsWindow? _importSettingsWindow;
    private SettingsImportedWindow? _settingsImportedWindow;

    private void MenuExportSettings_Click(object sender, RoutedEventArgs e) => _vm.RequestExportSettings();

    private void MenuImportSettings_Click(object sender, RoutedEventArgs e) => _vm.RequestImportSettings();

    private string? ProfileDir => (Application.Current as App)?.Profile?.ProfileDir ?? _vm.ProfileDirectory;

    private static bool BringForward(Window? existing)
    {
        if (existing is null) return false;
        existing.Activate();
        return true;
    }

    private void OpenExportSettings()
    {
        if (BringForward(_exportSettingsWindow) || ProfileDir is not { } dir) return;
        var restoreTo = Keyboard.FocusedElement;
        var vm = new ExportSettingsViewModel(
            dir,
            LoadLocalCalendarForExportAsync,
            _vm.BuildFolderPathsForExport(),
            Helpers.AppVersion.Display,
            Environment.MachineName);
        var window = new ExportSettingsWindow(vm) { Owner = this };
        window.SetFocusRestoreTarget(restoreTo);
        window.Closed += (_, _) => _exportSettingsWindow = null;
        _exportSettingsWindow = window;
        window.Show();
    }

    // Null when the local store cannot be read (--online), so the export says the calendar is missing
    // rather than writing an empty one.
    private async Task<IReadOnlyList<CalendarEvent>?> LoadLocalCalendarForExportAsync()
    {
        if (_vm.OnlineMode) return null;
        try
        {
            var events = await _localStore.LoadCalendarEventsAsync();
            return events.Where(ev => ev.AccountId == CalendarEvent.LocalAccountId).ToList();
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or System.IO.IOException)
        {
            LogService.Log("Export settings: Local Calendar", ex);
            return null;
        }
    }

    private void OpenImportSettings()
    {
        if (BringForward(_importSettingsWindow) || ProfileDir is not { } dir) return;
        var restoreTo = Keyboard.FocusedElement;
        var dialog = new OpenFileDialog
        {
            Title = "Import Settings",
            Filter = $"QuickMail settings (*{SettingsBundle.FileExtension})|*{SettingsBundle.FileExtension}|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        ImportSettingsViewModel vm;
        try
        {
            vm = ImportSettingsViewModel.Load(dialog.FileName, dir);
        }
        catch (SettingsBundleException ex)
        {
            MessageBox.Show(this, ex.Message, "Import Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            restoreTo?.Focus();
            return;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or System.IO.IOException or UnauthorizedAccessException)
        {
            // A profile file here that cannot be read stops the preview; the import would stop on it too.
            LogService.Log("Import settings: preview", ex);
            MessageBox.Show(this, $"The import could not be prepared. {ex.Message}", "Import Settings",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            restoreTo?.Focus();
            return;
        }

        var window = new ImportSettingsWindow(vm, RestartToApplyImport, CanRestartForImport) { Owner = this };
        window.SetFocusRestoreTarget(restoreTo);
        window.Closed += (_, _) => _importSettingsWindow = null;
        _importSettingsWindow = window;
        // Shown once the Open dialog has fully gone: shown straight away, Windows finished handing
        // activation back to this window afterwards and focus landed in the message list, not the preview
        // (found in VM testing).
        Dispatcher.BeginInvoke(() => { window.Show(); window.Activate(); }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// A restart closes every window without asking, so a message being written would be lost.
    /// Returns the reason it cannot restart yet, or null when it can.
    /// </summary>
    private string? CanRestartForImport() =>
        Application.Current.Windows.OfType<ComposeWindow>().Any()
            ? "Send, save or close the messages you are writing first. QuickMail restarts to import settings, and would close them without saving."
            : null;

    /// <summary>The staged import applies at the next start; restart now if a new process can be started.</summary>
    private void RestartToApplyImport()
    {
        // The profile in use, as a full path: a relative --profileDir would be resolved against
        // wherever the new process happens to start.
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (ProfileContext.ParseProfileDir(args) is not null && ProfileDir is { } dir)
            args = Helpers.AppRestart.WithProfileDir(args, dir);
        if (Helpers.AppRestart.StartReplacement(args))
        {
            RequestExit();
            return;
        }
        MessageBox.Show(this,
            "QuickMail could not restart itself. Your settings will be imported the next time QuickMail starts: close it and open it again.",
            "Import Settings", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>After a start that applied an import: the report and the accounts to sign in.</summary>
    private void ShowSettingsImportResult()
    {
        if (_settingsImportedWindow is not null || ProfileDir is not { } dir) return;
        var result = SettingsImporter.TakeResult(dir);
        if (result is null) return;
        var vm = new SettingsImportedViewModel(result, _vm.Accounts, _credentials, ReconnectForImportAsync);
        var window = new SettingsImportedWindow(vm) { Owner = this };
        window.SetFocusRestoreTarget(Keyboard.FocusedElement);
        window.Closed += (_, _) => _settingsImportedWindow = null;
        _settingsImportedWindow = window;
        // After startup has put focus in the message list (FocusActiveMessagePanel defers it), or that
        // would take focus back from this window (found in VM testing).
        Dispatcher.BeginInvoke(() => { window.Show(); window.Activate(); }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    // Reconnect Account, on the account as the list holds it now: Manage Accounts may have rebuilt the
    // list with new objects since the window opened.
    private async Task<bool> ReconnectForImportAsync(AccountModel account)
    {
        var current = _vm.Accounts.FirstOrDefault(a => a.Id == account.Id) ?? account;
        await _vm.ReconnectAccountCommand.ExecuteAsync(current);
        return current.IsConnected;
    }
}
