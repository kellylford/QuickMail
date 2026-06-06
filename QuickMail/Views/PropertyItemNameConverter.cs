using System;
using System.Globalization;
using System.Windows.Data;
using QuickMail.ViewModels;

namespace QuickMail.Views;

/// <summary>
/// Produces the AutomationProperties.Name for each row in the properties ListView.
/// Header rows return just the section name; data rows return "Label: Value".
/// </summary>
[ValueConversion(typeof(FlatRow), typeof(string))]
public sealed class PropertyItemNameConverter : IValueConverter
{
    public static readonly PropertyItemNameConverter Instance = new();

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is FlatRow row
            ? row.IsHeader ? row.Label : $"{row.Label}: {row.Value}"
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
