using System;
using System.Globalization;
using System.Windows.Data;

namespace QuickMail.Views;

/// <summary>
/// Builds a button label that carries a WPF access key, from a plain string supplied by a
/// ViewModel. The converter parameter is the label template, with <c>{0}</c> where the value
/// goes and a single underscore marking the access key — for example <c>"_Filter: {0}"</c>.
///
/// Underscores inside the value are doubled, because <c>ContentPresenter</c> renders content
/// through <c>AccessText</c>: an account named "work_mail" would otherwise swallow the
/// underscore and claim Alt+M as a second access key. That doubling rule is a View-layer
/// rendering convention, which is why it lives here rather than in the ViewModel — the VM
/// exposes the plain name and a separate plain accessible name.
/// </summary>
public sealed class AccessKeyLabelConverter : IValueConverter
{
    public static readonly AccessKeyLabelConverter Instance = new();

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text     = value?.ToString() ?? string.Empty;
        var template = parameter?.ToString() ?? "{0}";
        return string.Format(culture, template, text.Replace("_", "__"));
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
