using System;
using System.Collections.Generic;
using System.Linq;

namespace QuickMail.Models;

/// <summary>The kind of list row a spoken field layout applies to.</summary>
public enum RowKind
{
    /// <summary>An individual message row (flat list and the level-2 nodes of all three trees).</summary>
    Message,

    /// <summary>A conversation group header in the Conversations tree.</summary>
    Conversation,

    /// <summary>A sender group header in the From tree and the To tree.</summary>
    SenderGroup,
}

/// <summary>
/// How a state (boolean) field speaks. A field that should never speak is simply
/// disabled in the layout — there is deliberately no <c>Never</c> member here.
/// </summary>
public enum SpeakMode
{
    /// <summary>Speak the "true" word only when the state is true; stay silent otherwise.</summary>
    WhenTrue,

    /// <summary>Speak the "true" word or the "false" word, whichever applies.</summary>
    Always,
}

/// <summary>How a field's bound value is turned into spoken text.</summary>
public enum RowFieldFormat
{
    /// <summary>A string. Skipped entirely when null or whitespace.</summary>
    Text,

    /// <summary>A bool. Governed by <see cref="SpeakMode"/> and the true/false words.</summary>
    State,

    /// <summary>An int message count, spoken as "1 message" / "3 messages".</summary>
    Count,
}

/// <summary>
/// One field that may appear in a row's spoken string.
/// </summary>
/// <param name="Id">Stable identifier persisted in <c>rowlayout.json</c>. Never rename one of these.</param>
/// <param name="DisplayName">Label shown in the Message List Fields chooser.</param>
/// <param name="SpokenLabel">
/// Prefix spoken when "Speak field labels" is on. Applies to <see cref="RowFieldFormat.Text"/>
/// fields only — state and count fields already say what they are ("unread", "3 messages"),
/// so labelling them produces "Unread: unread".
/// </param>
/// <param name="BindingPath">Property path on the row's item, bound positionally by RowSpeech.</param>
/// <param name="Format">How the bound value becomes text.</param>
/// <param name="TrueWord">State fields: the word spoken when the state is true.</param>
/// <param name="FalseWord">
/// State fields under <see cref="SpeakMode.Always"/>: the word spoken when the state is false.
/// Null means the field stays silent when false even in Always mode.
/// </param>
public sealed record RowFieldDef(
    string Id,
    string DisplayName,
    string SpokenLabel,
    string BindingPath,
    RowFieldFormat Format,
    string? TrueWord = null,
    string? FalseWord = null)
{
    /// <summary>True when this field has a <see cref="SpeakMode"/> the user can choose.</summary>
    public bool IsState => Format == RowFieldFormat.State;
}

/// <summary>
/// The single source of truth for every field that can appear in a list row's spoken string.
/// Nothing else in the app may define a row field: the chooser UI, the binding superset installed
/// by <c>Views.RowSpeech</c>, and <c>Helpers.RowSpeechBuilder</c> all read from here.
/// </summary>
public static class RowFieldCatalog
{
    // ── Message rows ─────────────────────────────────────────────────────────
    //
    // Canonical order below is the BINDING order (the order RowSpeech adds bindings to the
    // MultiBinding and therefore the order values arrive in the converter). It is NOT the
    // default spoken order — see DefaultLayout. Append new fields at the END of this list.

    private static readonly RowFieldDef[] MessageFields =
    [
        new("flag",        "Flag",            "Flag",        "FlagLabel",       RowFieldFormat.Text),
        new("status",      "Status (combined)", "Status",    "ReadStatusLabel", RowFieldFormat.Text),
        new("attachments", "Attachments",     "Attachments", "HasAttachments",  RowFieldFormat.State, "attachments"),
        new("from",        "From",            "From",        "From",            RowFieldFormat.Text),
        new("subject",     "Subject",         "Subject",     "Subject",         RowFieldFormat.Text),
        new("preview",     "Preview",         "Preview",     "Preview",         RowFieldFormat.Text),
        new("date",        "Date",            "Date",        "DateDisplay",     RowFieldFormat.Text),
        new("unread",      "Unread",          "Read state",  "IsUnread",        RowFieldFormat.State, "unread", "read"),
        new("replied",     "Replied",         "Replied",     "IsReplied",       RowFieldFormat.State, "replied", "not replied"),
        new("forwarded",   "Forwarded",       "Forwarded",   "IsForwarded",     RowFieldFormat.State, "forwarded", "not forwarded"),
        new("to",          "To",              "To",          "To",              RowFieldFormat.Text),
        // Source location for aggregate views (#423). Already account-qualified when the aggregate
        // spans accounts, and stamped empty in single-folder views — so it self-skips there.
        new("folder",      "Source folder",   "Folder",      "FolderDisplayName", RowFieldFormat.Text),
        new("mailinglist", "Mailing list",    "Mailing list", "IsMailingList",  RowFieldFormat.State, "mailing list"),
    ];

    // ── Conversation group headers ───────────────────────────────────────────

    private static readonly RowFieldDef[] ConversationFields =
    [
        new("subject",   "Subject",       "Subject", "Subject",        RowFieldFormat.Text),
        new("count",     "Message count", "Count",   "Count",          RowFieldFormat.Count),
        new("sender",    "Sender",        "From",    "LastSenderName", RowFieldFormat.Text),
        new("flag",      "Flag",          "Flag",    "FlagLabel",      RowFieldFormat.Text),
        new("hasunread", "Has unread",    "Unread",  "HasUnread",      RowFieldFormat.State, "Has unread"),
        new("preview",   "Preview",       "Preview", "Preview",        RowFieldFormat.Text),
        new("date",      "Date",          "Date",    "DateDisplay",    RowFieldFormat.Text),
    ];

    // ── Sender / To group headers ────────────────────────────────────────────

    private static readonly RowFieldDef[] SenderGroupFields =
    [
        new("sender",        "Sender",        "From",           "SenderName",    RowFieldFormat.Text),
        new("count",         "Message count", "Count",          "Count",         RowFieldFormat.Count),
        new("flag",          "Flag",          "Flag",           "FlagLabel",     RowFieldFormat.Text),
        new("hasunread",     "Has unread",    "Unread",         "HasUnread",     RowFieldFormat.State, "Has unread"),
        new("preview",       "Preview",       "Preview",        "Preview",       RowFieldFormat.Text),
        new("date",          "Date",          "Date",           "DateDisplay",   RowFieldFormat.Text),
        new("newestsubject", "Newest subject", "Newest subject", "NewestSubject", RowFieldFormat.Text),
    ];

    // ── Default spoken order, by id ──────────────────────────────────────────
    //
    // These reproduce the strings the app has always spoken, so an existing user who never
    // opens the chooser hears exactly what they heard before. The one intentional difference
    // is that empty fields are now skipped instead of emitting a bare ". ".

    private static readonly string[] MessageDefaultOrder =
        ["flag", "status", "attachments", "from", "subject", "preview", "date", "folder"];

    private static readonly string[] ConversationDefaultOrder =
        ["subject", "count", "sender", "flag", "hasunread", "preview", "date"];

    private static readonly string[] SenderGroupDefaultOrder =
        ["sender", "count", "flag", "hasunread", "preview", "date"];

    /// <summary>Every field offered for a row kind, in canonical (binding) order.</summary>
    public static IReadOnlyList<RowFieldDef> For(RowKind kind) => kind switch
    {
        RowKind.Message      => MessageFields,
        RowKind.Conversation => ConversationFields,
        RowKind.SenderGroup  => SenderGroupFields,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Looks up a field definition by id, or null when the id is not in the catalog.</summary>
    public static RowFieldDef? Find(RowKind kind, string id) =>
        For(kind).FirstOrDefault(f => f.Id == id);

    /// <summary>
    /// Reads a real row object's values positionally, in the same order <c>Views.RowSpeech</c> binds
    /// them — so the Message List Fields preview can show what an actual message would say rather
    /// than a synthetic sample. Reflection is fine here: this runs once per preview refresh, never
    /// per rendered row (those go through real bindings).
    /// </summary>
    public static object?[] ValuesFor(RowKind kind, object item)
    {
        var type = item.GetType();
        return For(kind)
            .Select(f => type.GetProperty(f.BindingPath)?.GetValue(item))
            .ToArray();
    }

    /// <summary>
    /// The property paths <c>Views.RowSpeech</c> binds, in canonical order. The converter
    /// receives the values positionally in exactly this order.
    /// </summary>
    public static IReadOnlyList<string> BindingPathsFor(RowKind kind) =>
        For(kind).Select(f => f.BindingPath).ToArray();

    /// <summary>
    /// The shipped default layout for a row kind: the historical spoken order enabled, every
    /// other catalog field present but disabled so it can be turned on without a migration.
    /// </summary>
    /// <param name="announceFlagStatus">
    /// One-time honouring of the legacy <c>AnnounceFlagStatus</c> setting: when false, the flag
    /// field is seeded disabled. After the first save the layout owns this and the config key is
    /// no longer consulted.
    /// </param>
    public static List<RowFieldSetting> DefaultLayout(RowKind kind, bool announceFlagStatus = true)
    {
        var order = kind switch
        {
            RowKind.Message      => MessageDefaultOrder,
            RowKind.Conversation => ConversationDefaultOrder,
            RowKind.SenderGroup  => SenderGroupDefaultOrder,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var result = new List<RowFieldSetting>();
        foreach (var id in order)
        {
            var enabled = !(id == "flag" && !announceFlagStatus);
            result.Add(new RowFieldSetting { Id = id, Enabled = enabled, SpeakMode = SpeakMode.WhenTrue });
        }

        // Everything else in the catalog, disabled, in canonical order.
        foreach (var def in For(kind))
        {
            if (order.Contains(def.Id)) continue;
            result.Add(new RowFieldSetting { Id = def.Id, Enabled = false, SpeakMode = SpeakMode.WhenTrue });
        }

        return result;
    }

    /// <summary>All three default layouts.</summary>
    public static RowLayouts DefaultLayouts(bool announceFlagStatus = true) => new()
    {
        Message      = DefaultLayout(RowKind.Message, announceFlagStatus),
        Conversation = DefaultLayout(RowKind.Conversation),
        SenderGroup  = DefaultLayout(RowKind.SenderGroup),
    };
}
