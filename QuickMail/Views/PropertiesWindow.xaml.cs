using System;
using System.Globalization;
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
    }

    private void PropertyList_KeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            || e.Key == Key.Return)
        {
            if (sender is ListView lv && lv.SelectedItem is FlatRow row)
            {
                _vm.CopyRowCommand.Execute(row);
                e.Handled = true;
            }
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
