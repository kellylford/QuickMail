using System;
using System.Globalization;
using System.Windows.Data;

namespace QuickMail.Views;

/// <summary>
/// Binds a radio button's <c>IsChecked</c> to a string value it represents, given as the
/// <c>ConverterParameter</c>. Convert is true when the value equals the parameter, ignoring case
/// (colors are hex strings, and "#e53e3e" is the same color as "#E53E3E"). ConvertBack writes the
/// parameter when the button is checked and leaves the source alone when it is unchecked: the
/// button that is checked next does the writing (issue #1080, the Flag Manager color buttons).
/// </summary>
public sealed class StringEqualsParameterConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && parameter is string p
           && string.Equals(s, p, StringComparison.OrdinalIgnoreCase);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && parameter is string p ? p : Binding.DoNothing;
}
