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
/// etc.) and renders field/value sections plus optional sub-lists and raw-headers expander.
/// </summary>
public partial class PropertiesWindow : Window
{
    private readonly PropertiesViewModel _vm;

    public PropertiesWindow(PropertiesViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        vm.AnnouncementRequested += OnAnnouncement;
        Closed += (_, _) => vm.AnnouncementRequested -= OnAnnouncement;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Focus the first ListView in the first field section so the user can
        // read properties immediately with arrow keys, without pressing Tab first.
        FocusFirstListView();
    }

    private void FocusFirstListView()
    {
        // Walk the visual tree to find the first ListView inside the ItemsControl.
        // We look for a ListView in the FieldSections ItemsControl.
        var firstList = FindFirstListView(FieldSectionsControl);
        firstList?.Focus();
    }

    // Walks the visual tree depth-first to find the first ListView child.
    private static ListView? FindFirstListView(DependencyObject? root)
    {
        if (root is null) return null;
        if (root is ListView lv) return lv;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            var found = FindFirstListView(child);
            if (found is not null) return found;
        }
        return null;
    }

    // Ctrl+C or Enter on a focused row copies "Label: Value" to the clipboard.
    private void PropertyList_KeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            || e.Key == Key.Return)
        {
            if (sender is ListView lv && lv.SelectedItem is PropertyItem item)
            {
                _vm.CopyRowCommand.Execute(item);
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
