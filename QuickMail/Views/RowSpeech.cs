using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Data;
using QuickMail.Helpers;
using QuickMail.Models;

namespace QuickMail.Views;

/// <summary>
/// Installs a list row's <see cref="AutomationProperties.NameProperty"/> binding from the
/// catalog, so the spoken field list is declared in exactly one place instead of being
/// hand-maintained at every row template in the app.
///
/// <para>Set <c>RowSpeech.Kind</c> on an <em>item container style</em> (ListViewItem /
/// TreeViewItem) — not on a DataTemplate. The name must land on the container, because that is
/// the element a screen reader lands on when the user arrows through the list.</para>
///
/// <para>Why a MultiBinding over every candidate property rather than one computed property on
/// the model: the row must re-speak when any contributing value changes (marking read, applying
/// a flag, a preview arriving), and binding each property individually is what gets us that
/// change notification for free. The final binding is the user's layout, hung off the window's
/// DataContext — assigning a new <see cref="RowSpeechSettings"/> there re-speaks every realized
/// row at once, including recycled virtualized containers, with no list reload.</para>
/// </summary>
public static class RowSpeech
{
    /// <summary>Which row kind this container is, and therefore which catalog and layout apply.</summary>
    public static readonly DependencyProperty KindProperty =
        DependencyProperty.RegisterAttached(
            "Kind", typeof(RowKind?), typeof(RowSpeech),
            new PropertyMetadata(null, OnKindChanged));

    public static RowKind? GetKind(DependencyObject obj) => (RowKind?)obj.GetValue(KindProperty);
    public static void SetKind(DependencyObject obj, RowKind? value) => obj.SetValue(KindProperty, value);

    // One converter per kind: the converter has to know which catalog the positional values
    // belong to, and a Style setter cannot pass that through.
    private static readonly Dictionary<RowKind, RowSpeechConverter> Converters = new()
    {
        [RowKind.Message]      = new RowSpeechConverter(RowKind.Message),
        [RowKind.Conversation] = new RowSpeechConverter(RowKind.Conversation),
        [RowKind.SenderGroup]  = new RowSpeechConverter(RowKind.SenderGroup),
    };

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not RowKind kind)
        {
            BindingOperations.ClearBinding(d, AutomationProperties.NameProperty);
            return;
        }

        var mb = new MultiBinding { Converter = Converters[kind], Mode = BindingMode.OneWay };

        // Positional, in catalog order — RowSpeechBuilder maps them back to field ids.
        foreach (var path in RowFieldCatalog.BindingPathsFor(kind))
            mb.Bindings.Add(new Binding(path) { Mode = BindingMode.OneWay });

        // …and the layout itself, last.
        mb.Bindings.Add(new Binding("DataContext.RowSpeech")
        {
            Mode = BindingMode.OneWay,
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Window), 1),
        });

        BindingOperations.SetBinding(d, AutomationProperties.NameProperty, mb);
    }
}

/// <summary>
/// Turns the bound row values plus the user's layout into the spoken string. All of the actual
/// composition lives in <see cref="RowSpeechBuilder"/>, which is pure and unit tested; this is
/// only the WPF adapter.
/// </summary>
public sealed class RowSpeechConverter : IMultiValueConverter
{
    private readonly RowKind _kind;

    public RowSpeechConverter(RowKind kind) => _kind = kind;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        // The layout occupies the last slot; anything shorter means the bindings have not been
        // wired up yet (or are being torn down), so say nothing rather than a partial string.
        if (values is null || values.Length < 2) return string.Empty;

        var settings = values[^1] as RowSpeechSettings ?? RowSpeechSettings.Default;

        var fieldValues = new object?[values.Length - 1];
        Array.Copy(values, fieldValues, fieldValues.Length);

        return RowSpeechBuilder.Build(
            _kind, fieldValues, settings.Layouts.For(_kind), settings.ShowLabels);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
