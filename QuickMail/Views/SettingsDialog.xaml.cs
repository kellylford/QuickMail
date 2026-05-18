using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
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

    private void HotkeyListView_SelectionChanged(object sender, object e)
    {
        // Selection changed; buttons will handle enable state binding
    }

    private void SetShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedHotkey == null) return;

        while (true)
        {
            var captureDialog = new KeyCaptureDialog { Owner = this };
            if (captureDialog.ShowDialog() != true)
            {
                return;
            }

            var key = captureDialog.CapturedKey;
            var modifiers = captureDialog.CapturedModifiers;

            var conflict = _vm.FindConflict(key, modifiers);
            if (conflict != null)
            {
                var conflictDialog = new ConflictDialog(conflict.Title) { Owner = this };
                if (conflictDialog.ShowDialog() == true)
                {
                    // "Choose Another" — loop back to capture dialog
                    continue;
                }
                else
                {
                    // "Cancel" — exit without saving
                    return;
                }
            }

            _vm.SelectedHotkey.SetCustomBinding(key, modifiers);
            break;
        }
    }

    private void RestoreDefaultButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedHotkey == null) return;
        _vm.SelectedHotkey.ClearCustomBinding();
    }
}
