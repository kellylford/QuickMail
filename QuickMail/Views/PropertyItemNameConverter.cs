using System;
using System.Globalization;
using System.Windows.Data;
using QuickMail.Models;

namespace QuickMail.Views;

/// <summary>
/// Collapses a PropertyItem's Label and Value into a single "Label: Value" string
/// for AutomationProperties.Name on ListViewItem containers, so screen readers
/// announce a natural English field description without re-reading column headers.
/// </summary>
[ValueConversion(typeof(PropertyItem), typeof(string))]
public sealed class PropertyItemNameConverter : IValueConverter
{
    public static readonly PropertyItemNameConverter Instance = new();

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is PropertyItem item ? $"{item.Label}: {item.Value}" : string.Empty;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
