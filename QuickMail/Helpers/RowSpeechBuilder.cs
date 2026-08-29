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
    private const string Separator = ". ";

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
    public static string Build(
        RowKind kind,
        IReadOnlyList<object?> values,
        IReadOnlyList<RowFieldSetting>? layout,
        bool showLabels = false)
    {
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

            if (sb.Length > 0) sb.Append(Separator);
            sb.Append(part);
        }

        if (sb.Length == 0) return string.Empty;
        sb.Append('.');
        return sb.ToString();
    }

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

            case RowFieldFormat.Phrase:
            {
                // Self-describing, so unlabelled like the state and count fields.
                return (raw as string ?? string.Empty).Trim();
            }

            case RowFieldFormat.Count:
            {
                if (raw is not int n) return string.Empty;
                // Self-describing, so unlabelled like the state fields.
                return string.Create(CultureInfo.CurrentCulture, $"{n} {(n == 1 ? "message" : "messages")}");
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
