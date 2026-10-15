using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using QuickMail.Models;

namespace QuickMail.Helpers;

/// <summary>
/// Builds a list row's spoken string from the user's field layout. Deliberately a pure function
/// over its arguments — no services, no I/O, no UI thread — so the entire speech surface can be
/// unit tested without standing up WPF. <c>Views.RowSpeech</c> supplies the bound values;
/// this decides what is said.
/// </summary>
public static class RowSpeechBuilder
{
    /// <summary>
    /// Composes the spoken string for one row.
    /// </summary>
    /// <param name="kind">Which row kind, and therefore which catalog and which layout.</param>
    /// <param name="values">
    /// The bound values, positionally in <see cref="RowFieldCatalog.For"/> order for
    /// <paramref name="kind"/>. Shorter arrays are tolerated; missing entries read as absent.
    /// </param>
    /// <param name="layout">
    /// The user's ordered field settings for this kind. Null or empty falls back to the shipped
    /// default, so a row never goes silent because the layout failed to load.
    /// </param>
    /// <param name="showLabels">Whether to prefix text-valued fields with their spoken label.</param>
    /// <param name="separator">
    /// What goes between fields (#802). Only <see cref="RowFieldSeparator.Period"/> also ends the
    /// row with a period, which is what every row said before the choice existed.
    /// </param>
    public static string Build(
        RowKind kind,
        IReadOnlyList<object?> values,
        IReadOnlyList<RowFieldSetting>? layout,
        bool showLabels = false,
        RowFieldSeparator separator = RowFieldSeparator.Period)
    {
        var between   = RowFieldSeparators.Between(separator);
        var catalog   = RowFieldCatalog.For(kind);
        var effective = layout is { Count: > 0 } ? layout : RowFieldCatalog.DefaultLayout(kind);

        // Positional values -> field id, so the layout can address them in any order.
        var byId = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (int i = 0; i < catalog.Count; i++)
            byId[catalog[i].Id] = i < values.Count ? Normalize(values[i]) : null;

        var sb = new StringBuilder();
        foreach (var setting in effective)
        {
            if (!setting.Enabled) continue;
            if (RowFieldCatalog.Find(kind, setting.Id) is not { } def) continue;
            if (!byId.TryGetValue(def.Id, out var raw)) continue;

            var part = Render(def, setting, raw, showLabels);
            if (string.IsNullOrWhiteSpace(part)) continue;

            if (sb.Length > 0) sb.Append(Join(separator, between, sb[^1]));
            sb.Append(part);
        }

        if (sb.Length == 0) return string.Empty;
        if (RowFieldSeparators.EndsWithPeriod(separator)) sb.Append('.');
        return sb.ToString();
    }

    /// <summary>
    /// The text between two fields. A comma after a field that already ends in its own punctuation
    /// ("…required today.", "Lunch tomorrow?") would read ".," — punctuation on punctuation, from a
    /// choice made to have less of it — so there the comma gives way to a plain space. The period
    /// choice is left exactly as rows have always read, doubled period included (#802).
    /// </summary>
    private static string Join(RowFieldSeparator separator, string between, char previous) =>
        separator == RowFieldSeparator.Comma && previous is '.' or '?' or '!' or ':' or ';'
            ? " "
            : between;

    /// <summary>
    /// WPF hands unset or failed bindings through as <see cref="System.Windows.DependencyProperty.UnsetValue"/>
    /// during template setup; treat those as absent rather than stringifying them into speech.
    /// </summary>
    private static object? Normalize(object? value) =>
        value == System.Windows.DependencyProperty.UnsetValue ? null : value;

    private static string Render(RowFieldDef def, RowFieldSetting setting, object? raw, bool showLabels)
    {
        switch (def.Format)
        {
            case RowFieldFormat.State:
            {
                var on = raw is bool b && b;
                // State words already say what they are ("unread", "attachments"), so they are
                // never labelled — "Unread: unread" is noise, not information.
                if (on) return def.TrueWord ?? string.Empty;
                return setting.SpeakMode == SpeakMode.Always ? def.FalseWord ?? string.Empty : string.Empty;
            }

            case RowFieldFormat.Count:
            {
                if (raw is not int n) return string.Empty;
                // Self-describing, so unlabelled like the state fields.
                return string.Create(CultureInfo.CurrentCulture, $"{n} {(n == 1 ? "message" : "messages")}");
            }

            // Both unread forms say nothing when nothing is unread, the way Has unread does: a row
            // that is all read stays as short as it was (#800). Self-describing, so unlabelled.
            case RowFieldFormat.UnreadCount:
            {
                if (raw is not int n || n <= 0) return string.Empty;
                return string.Create(CultureInfo.CurrentCulture, $"{n} unread");
            }

            case RowFieldFormat.UnreadOfTotal:
            {
                if (raw is not UnreadTally { Unread: > 0 } t) return string.Empty;
                return string.Create(CultureInfo.CurrentCulture, $"{t.Unread} of {t.Total} unread");
            }

            default:
            {
                var value = (raw as string ?? string.Empty).Trim();
                if (value.Length == 0) return string.Empty;
                return showLabels ? $"{def.SpokenLabel}: {value}" : value;
            }
        }
    }
}
