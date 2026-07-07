using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// Reusable read-only properties dialog. Accepts a <see cref="PropertiesViewModel"/>
/// built by one of the context builders (MessagePropertiesBuilder, FolderPropertiesBuilder,
/// etc.) and renders all property rows in a single grouped ListView plus an optional
/// raw-headers expander.
/// </summary>
public partial class PropertiesWindow : Window
{
    private readonly PropertiesViewModel _vm;

    public PropertiesWindow(PropertiesViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

        vm.AnnouncementRequested += OnAnnouncement;
        Closed += (_, _) => vm.AnnouncementRequested -= OnAnnouncement;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PropertiesList.Focus();
        AccessibilityHelper.Announce(this,
            "Press Enter or Ctrl+C to copy the selected row.",
            category: AnnouncementCategory.Hint);
    }

    private void PropertyList_KeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            || e.Key == Key.Return)
        {
            if (sender is not ListView lv) return;

            var selected = lv.SelectedItems.OfType<FlatRow>().ToList();

            if (selected.Count == 0) return;

            if (selected.Count == 1)
                _vm.CopyRowCommand.Execute(selected[0]);
            else
            {
                var text = string.Join(Environment.NewLine,
                    selected.Select(r => r.IsHeader ? r.Label : $"{r.Label}: {r.Value}"));
                Clipboard.SetText(text);
                AccessibilityHelper.Announce(this,
                    $"{selected.Count} rows copied",
                    category: AnnouncementCategory.Result);
            }

            e.Handled = true;
        }
    }

    private void OnAnnouncement(string text, AnnouncementCategory category) =>
        AccessibilityHelper.Announce(this, text, category: category);
}

/// <summary>
/// Returns Visibility.Collapsed when the bound value is null, Visible otherwise.
/// Used to hide the Raw headers Expander when no raw headers are available.
/// </summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
