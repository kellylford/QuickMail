using System;
using System.Globalization;
using System.Windows.Data;
using QuickMail.Models;

namespace QuickMail.Views;

/// <summary>
/// For a <see cref="GroupModel"/>, returns the portion of <c>Display</c>
/// that follows the group's name (e.g. ", 3 members" or ", 2 members (1
/// missing)"). Used by the Groups list template so the bold name and the
/// member-count hint are visually distinct but the screen reader hears
/// the full <c>Display</c> string as a single accessible name.
/// </summary>
public sealed class GroupSubtitleConverter : IValueConverter
{
    public static readonly GroupSubtitleConverter Instance = new();

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not GroupModel g) return string.Empty;
        var display = g.Display ?? string.Empty;
        if (display.StartsWith(g.Name, StringComparison.Ordinal))
        {
            var tail = display.Substring(g.Name.Length).TrimStart();
            return tail;
        }
        return display;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
