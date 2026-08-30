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

    /// <summary>
    /// A string that already says what it is, so it is never labelled — the rule
    /// <see cref="State"/> and <see cref="Count"/> follow, for a field whose wording depends
    /// on more than a bool. Skipped when empty.
    /// </summary>
    Phrase,
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
        // One word for four states, by priority: replied > forwarded > unread > read. Off by
        // default since #558 — it cannot say "unread but not read" (it is Text, so it has no
        // SpeakMode), and its priority chain hides unread on a replied or forwarded message.
        // The three fields below say the same things separately and each carry a SpeakMode.
        new("status",      "Read status (combined)", "Status", "ReadStatusLabel", RowFieldFormat.Text),
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
        // Watched-conversation membership. Ships disabled (absent from MessageDefaultOrder), so an
        // existing user's spoken row text is unchanged until they enable it in the fields chooser.
        new("watched",     "Watched",         "Watched",     "IsWatched",       RowFieldFormat.State, "watched"),
        // Where the message IS, rather than anything about it (#637). Ships ENABLED and is
        // force-enabled once for existing layouts — see MessageForceEnabledOnUpgrade. Off by
        // default would mean the only durable signal that a draft has not left this computer never
        // reaches anyone who already has a saved layout, which is everyone who has ever opened the
        // fields chooser.
        //
        // A Phrase over LocationLabel, not a State over a bool: one bool cannot tell "on its way"
        // from "stuck until you act", and a field that went silent the moment the server refused
        // the draft was indistinguishable from one that had uploaded fine.
        new("notonserver", "Not on server",   "Not on server", "LocationLabel",    RowFieldFormat.Phrase),
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
    // The group orders reproduce the strings the app has always spoken. The message order does
    // too for an unread message, but deliberately no longer does for a read one: #558 reported
    // that the app said "read" on every read row with no way to switch that off, because the
    // combined "status" field owned the wording and, being a Text field, has no SpeakMode.
    //
    // Decomposing it into unread/replied/forwarded — each a State field, each defaulting to
    // "speak only when true" — means an unread row still says "unread" in the same position, a
    // read row says nothing about read state, and a replied-but-unread row says both instead of
    // only "replied". Anyone who wants the old single word turns "status" back on and these off.
    //
    // Only users with no rowlayout.json see this change; Reconcile never re-enables a field a
    // saved layout already covers.

    // "not on server" leads the row, ahead of even flag and unread. Where a message is outranks
    // everything else the row can say about it, and the field is empty — costing nothing to arrow
    // past — for every message that IS on the server. Chosen by the user (#637).
    private static readonly string[] MessageDefaultOrder =
        ["notonserver",
         "flag", "unread", "replied", "forwarded", "attachments",
         "from", "subject", "preview", "date", "folder"];

    /// <summary>
    /// Fields added after users already had saved layouts, which must be switched on for them
    /// anyway (#637). A field the user has never seen carries no preference of theirs to override,
    /// and this one is the only signal that a message is not where they think it is.
    /// <para>Applied exactly once per id: <see cref="RowFieldLayout.Reconcile"/> records the ids it
    /// has introduced, so a field the user later turns OFF stays off.</para>
    /// </summary>
    internal static readonly string[] MessageForceEnabledOnUpgrade = ["notonserver"];

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
        // Already on here, by virtue of being in MessageDefaultOrder — so record it as
        // introduced straight away. Without this, a user who starts from defaults, turns the
        // field OFF and saves gets it switched back on at the next launch, because the
        // one-time enable had never been recorded as having happened (#637).
        IntroducedFields = [.. MessageForceEnabledOnUpgrade],
    };
}
