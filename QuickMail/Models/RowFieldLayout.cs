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

    /// <summary>
    /// Ids that have been switched on once for an existing layout (#637). Persisted, and
    /// that is the whole point: it is what makes the one-time enable exactly once, so a user
    /// who hears "not on server" and decides they would rather not does not have it switched
    /// back on at every launch.
    /// </summary>
    public List<string> IntroducedFields { get; set; } = [];
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

        IntroduceOnce();
    }

    /// <summary>
    /// Switches on fields added after this file was written, once each (#637).
    /// <para>New catalog fields normally arrive disabled, which is right for optional extras: the
    /// file represents the user's choices and should not be overridden. It is wrong for a field
    /// that reports a message is not where the user thinks it is — off by default there means the
    /// signal never reaches anyone who already has a layout, which is everyone who has opened the
    /// fields chooser. A field the user has never seen carries no preference to override.</para>
    /// <para>Once introduced the id is recorded and never touched again, so turning it off
    /// afterwards sticks.</para>
    /// </summary>
    private void IntroduceOnce()
    {
        var already = new HashSet<string>(IntroducedFields, StringComparer.Ordinal);

        // Not for a layout still using the combined "status" field. That one speaks the same state
        // through ReadStatusLabel — "saved on this computer, not yet on the server" — so switching
        // this on as well makes every such row say the same thing twice. Still recorded as
        // introduced, so turning "status" off later does not switch a field on by surprise.
        var combined = Message.Find(f => string.Equals(f.Id, "status", StringComparison.Ordinal));
        var haveCombined = combined?.Enabled == true;

        foreach (var id in RowFieldCatalog.MessageForceEnabledOnUpgrade)
        {
            if (!already.Add(id)) continue;
            IntroducedFields.Add(id);
            if (haveCombined) continue;

            var field = Message.Find(f => string.Equals(f.Id, id, StringComparison.Ordinal));
            if (field != null) field.Enabled = true;
        }
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

        // The introduced fields go to the FRONT of an existing layout, not the end. They are
        // switched on from the moment the file is read, so where they land is where they are
        // HEARD — and appending put "not on server" after the preview, the date and the source
        // folder for exactly the users who never chose it, while a fresh layout says it first.
        // This does reorder a row the user arranged, which is why it is limited to these and
        // happens once. Chosen by the user over the appended alternative (#637).
        var lead = kind == RowKind.Message ? RowFieldCatalog.MessageForceEnabledOnUpgrade : [];
        var at = 0;
        foreach (var id in lead)
        {
            if (seen.Contains(id) || RowFieldCatalog.Find(kind, id) is null) continue;
            seen.Add(id);
            result.Insert(at++, new RowFieldSetting { Id = id, Enabled = false, SpeakMode = SpeakMode.WhenTrue });
        }

        // Everything else the file did not mention is appended disabled, which is right for an
        // optional extra the user has expressed no view about: it costs them nothing and moves
        // nothing they arranged.
        foreach (var def in RowFieldCatalog.For(kind))
        {
            if (seen.Contains(def.Id)) continue;
            result.Add(new RowFieldSetting { Id = def.Id, Enabled = false, SpeakMode = SpeakMode.WhenTrue });
        }

        return result;
    }
}
