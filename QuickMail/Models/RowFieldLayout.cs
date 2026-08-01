using System;
using System.Collections.Generic;

namespace QuickMail.Models;

/// <summary>
/// One field's placement in a row's spoken string. The field's position in the containing
/// list <em>is</em> its spoken order — there is no sort key, because the whole list is
/// rewritten on every change.
/// </summary>
public sealed class RowFieldSetting
{
    /// <summary>Catalog id. Ids not present in <see cref="RowFieldCatalog"/> are dropped on load.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Whether this field is spoken at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>State fields only; ignored for text and count fields.</summary>
    public SpeakMode SpeakMode { get; set; } = SpeakMode.WhenTrue;

    public RowFieldSetting Clone() => new() { Id = Id, Enabled = Enabled, SpeakMode = SpeakMode };
}

/// <summary>
/// Everything the row renderer needs to compose a spoken string, in one object so each row
/// carries a single extra binding. Replaced wholesale (never mutated) when the user changes the
/// layout, so raising PropertyChanged on the owning property re-speaks every realized row.
/// </summary>
public sealed record RowSpeechSettings(RowLayouts Layouts, bool ShowLabels)
{
    /// <summary>Shipped defaults, used before the service loads and by tests.</summary>
    public static RowSpeechSettings Default { get; } = new(RowFieldCatalog.DefaultLayouts(), false);
}

/// <summary>
/// The user's spoken field layouts, one per row kind. Persisted as <c>rowlayout.json</c>.
/// </summary>
public sealed class RowLayouts
{
    public List<RowFieldSetting> Message      { get; set; } = [];
    public List<RowFieldSetting> Conversation { get; set; } = [];
    public List<RowFieldSetting> SenderGroup  { get; set; } = [];

    /// <summary>The layout for a row kind.</summary>
    public List<RowFieldSetting> For(RowKind kind) => kind switch
    {
        RowKind.Message      => Message,
        RowKind.Conversation => Conversation,
        RowKind.SenderGroup  => SenderGroup,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Replaces the layout for a row kind.</summary>
    public void Set(RowKind kind, List<RowFieldSetting> fields)
    {
        switch (kind)
        {
            case RowKind.Message:      Message      = fields; break;
            case RowKind.Conversation: Conversation = fields; break;
            case RowKind.SenderGroup:  SenderGroup  = fields; break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    /// <summary>A deep copy. Used to hand the renderer an immutable-by-convention snapshot.</summary>
    public RowLayouts Clone() => new()
    {
        Message      = Message.ConvertAll(f => f.Clone()),
        Conversation = Conversation.ConvertAll(f => f.Clone()),
        SenderGroup  = SenderGroup.ConvertAll(f => f.Clone()),
    };

    /// <summary>
    /// Brings a loaded layout back in line with the catalog, in place:
    /// duplicate and unknown ids are dropped (a file written by a newer version), and catalog
    /// fields the file does not mention are appended disabled (a field added since the file was
    /// written). The user's existing order is never disturbed.
    /// </summary>
    public void Reconcile()
    {
        foreach (var kind in new[] { RowKind.Message, RowKind.Conversation, RowKind.SenderGroup })
            Set(kind, Reconcile(kind, For(kind)));
    }

    private static List<RowFieldSetting> Reconcile(RowKind kind, List<RowFieldSetting>? loaded)
    {
        var result = new List<RowFieldSetting>();
        var seen   = new HashSet<string>(StringComparer.Ordinal);

        foreach (var setting in loaded ?? [])
        {
            if (string.IsNullOrEmpty(setting.Id)) continue;
            if (RowFieldCatalog.Find(kind, setting.Id) is null) continue;   // unknown id
            if (!seen.Add(setting.Id)) continue;                            // duplicate
            result.Add(setting);
        }

        foreach (var def in RowFieldCatalog.For(kind))
        {
            if (seen.Contains(def.Id)) continue;
            result.Add(new RowFieldSetting { Id = def.Id, Enabled = false, SpeakMode = SpeakMode.WhenTrue });
        }

        return result;
    }
}
