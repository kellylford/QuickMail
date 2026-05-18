using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.Helpers;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// Converts an empty string to "—" (dash) for display purposes.
/// </summary>
public class EmptyStringToDashConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var str = value as string;
        return string.IsNullOrEmpty(str) ? "—" : str;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public partial class SettingsDialog : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsDialog(SettingsViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Defer focus until after the window is activated so the screen reader picks it up.
        Dispatcher.InvokeAsync(() => PreviewLinesCombo.Focus(), DispatcherPriority.Input);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _vm.SaveCommand.Execute(null);
        DialogResult = true;
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
            e.Handled = true;
        }
    }

    private void HotkeyListView_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateActionButtons();
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

        while (true)
        {
            var captureDialog = new KeyCaptureDialog { Owner = this };
            if (captureDialog.ShowDialog() != true)
            {
                LogService.Debug("[Settings] Key capture cancelled");
                return;
            }

            var key      = captureDialog.CapturedKey;
            var modifiers = captureDialog.CapturedModifiers;
            var gesture  = GestureHelper.Format(key, modifiers);
            LogService.Debug($"[Settings] Captured: key={key} modifiers={modifiers} gesture='{gesture}'");

            var conflict = _vm.FindConflict(key, modifiers);
            if (conflict != null && conflict != row)
            {
                LogService.Debug($"[Settings] Conflict with '{conflict.CommandId}'");
                var conflictDialog = new ConflictDialog(conflict.Title) { Owner = this };
                if (conflictDialog.ShowDialog() == true)
                    continue;
                else
                {
                    LogService.Debug("[Settings] Conflict cancelled");
                    return;
                }
            }

            row.SetCustomBinding(key, modifiers);
            UpdateActionButtons();
            LogService.Debug($"[Settings] Assigned '{gesture}' to '{row.Title}' — announcing");
            AnnounceAssignment(gesture, row.Title);
            break;
        }
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
