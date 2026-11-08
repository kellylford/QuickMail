using System;
using System.Collections.Generic;
using System.Linq;

namespace QuickMail.Models;

/// <summary>Something that happens in QuickMail that can have a sound (issue #912).</summary>
public enum SoundEvent
{
    MessageSent,
    MessageQueued,
    SendFailed,
    NewMail,
    WatchedMail,
    ActionFailed,
    ConnectionLost,
    ConnectionRestored,
    CalendarReminder,
    MessageDeleted,
    MessageArchived,
    MessageMoved,
    TrashEmptied,

    /// <summary>A rule set to the default sound matched new mail (#932). Played through <c>PlayRule</c>, not <c>Play</c>.</summary>
    RuleMatched,
}

/// <summary>Why a sound is playing, which decides what can hold it back.</summary>
public enum SoundOrigin
{
    /// <summary>Answers something the person just did, so it plays even in a presentation.</summary>
    Direct,

    /// <summary>Happened on its own, so it stays quiet during a presentation or full-screen use.</summary>
    Background,

    /// <summary>Background, and gives way to a rule sound for the same mail.</summary>
    Arrival,

    /// <summary>A rule's own sound. Background, and holds back the arrival sound that follows it.</summary>
    Rule,
}

/// <summary>One event's place in Settings and config.ini.</summary>
/// <param name="Event">The event.</param>
/// <param name="ConfigKey">Its key in the [sounds] section of config.ini.</param>
/// <param name="Label">What Settings calls it.</param>
/// <param name="BuiltInSound">The built-in sound it uses unless the person chooses another.</param>
/// <param name="Origin">Why it plays.</param>
public sealed record SoundEventInfo(
    SoundEvent Event, string ConfigKey, string Label, string BuiltInSound, SoundOrigin Origin);

/// <summary>A tone of built-in sound: a folder of WAVs. A screen reader reads the item from <see cref="ToString"/>.</summary>
/// <param name="Id">Its folder under Sounds/BuiltIn, and the id of the built-in scheme that uses it.</param>
/// <param name="Label">What Settings calls it.</param>
public sealed record SoundPack(string Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Every sound event and every built-in sound.</summary>
public static class SoundCatalog
{
    /// <summary>The sound a new rule starts with.</summary>
    public const string DefaultRuleSound = "matched";

    /// <summary>The pack, and built-in scheme, used until someone chooses another, or if config.ini names one this build doesn't have.</summary>
    public const string DefaultPack = "glass";

    /// <summary>The id of every scheme of the person's own starts with this, then a GUID (#932).</summary>
    public const string UserSchemePrefix = "user-";

    /// <summary>The longest name a scheme of the person's own may have.</summary>
    public const int MaxSchemeNameLength = 60;

    /// <summary>Every pack has every sound, with the same melody; only the tone differs. Each is also a built-in scheme.</summary>
    public static readonly IReadOnlyList<SoundPack> Packs =
    [
        new("glass", "Glass"),
        new("softbell", "Soft bell"),
        new("wood", "Wood"),
    ];

    /// <summary>
    /// The folder under the profile that holds custom sounds: a rule's own files directly in it, and
    /// each scheme's in a folder of its own inside it (<see cref="SchemeFolderName"/>, #948).
    /// </summary>
    public const string CustomFolderName = "sounds";

    /// <summary>The largest custom sound accepted, whether chosen in Settings or arriving in a settings file.</summary>
    public const long MaxCustomFileBytes = 5L * 1024 * 1024;

    /// <summary>The most custom sound files a settings file can bring in. Each is a file on disk, so it needs its own limit.</summary>
    public const int MaxCustomFilesInImport = 100;

    /// <summary>The longest any sound may play.</summary>
    public static readonly TimeSpan MaxCustomSoundLength = TimeSpan.FromSeconds(30);

    public static readonly IReadOnlyList<SoundEventInfo> Events =
    [
        new(SoundEvent.MessageSent,        "MessageSent",        "Message sent",               "sent",         SoundOrigin.Direct),
        new(SoundEvent.MessageQueued,      "MessageQueued",      "Message queued",             "queued",       SoundOrigin.Direct),
        new(SoundEvent.SendFailed,         "SendFailed",         "Send failed",                "failed",       SoundOrigin.Direct),
        new(SoundEvent.NewMail,            "NewMail",            "New mail",                   "arrived",      SoundOrigin.Arrival),
        new(SoundEvent.WatchedMail,        "WatchedMail",        "Watched conversation mail",  "attention",    SoundOrigin.Arrival),
        new(SoundEvent.ActionFailed,       "ActionFailed",       "Delete, move, archive or empty trash failed", "error",    SoundOrigin.Direct),
        new(SoundEvent.ConnectionLost,     "ConnectionLost",     "Connection lost",            "disconnected", SoundOrigin.Background),
        new(SoundEvent.ConnectionRestored, "ConnectionRestored", "Connection restored",        "connected",    SoundOrigin.Background),
        new(SoundEvent.CalendarReminder,   "CalendarReminder",   "Calendar reminder",          "reminder",     SoundOrigin.Background),
        new(SoundEvent.MessageDeleted,     "MessageDeleted",     "Message deleted",            "removed",      SoundOrigin.Direct),
        new(SoundEvent.MessageArchived,    "MessageArchived",    "Message archived",           "filed",        SoundOrigin.Direct),
        new(SoundEvent.MessageMoved,       "MessageMoved",       "Message moved",              "moved",        SoundOrigin.Direct),
        new(SoundEvent.TrashEmptied,       "TrashEmptied",       "Trash emptied",              "emptied",      SoundOrigin.Direct),
        new(SoundEvent.RuleMatched,        "RuleMatched",        "A rule matched",             DefaultRuleSound, SoundOrigin.Rule),
    ];

    /// <summary>Every built-in sound name, one per event.</summary>
    public static readonly IReadOnlyList<string> BuiltInSounds = [.. Events.Select(e => e.BuiltInSound).Distinct()];

    private static readonly Dictionary<SoundEvent, SoundEventInfo> ByEvent = Events.ToDictionary(e => e.Event);

    public static SoundEventInfo Info(SoundEvent e) => ByEvent[e];

    /// <summary>Identifies a message between the rule that sounded for it and the new-mail sound.</summary>
    public static string MessageKey(Guid accountId, string messageId) => $"{accountId:N}|{messageId}";

    /// <summary>What Settings calls a built-in sound: "arrived" is "Arrived".</summary>
    public static string BuiltInLabel(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

    /// <summary>A rule's "default sound": the unqualified built-in rule sound, which is what every rule stored before #932.</summary>
    public static bool IsDefaultRuleSound(SoundRef sound) =>
        sound.Kind == SoundRefKind.BuiltIn && sound.Pack is null && sound.Name == DefaultRuleSound;

    /// <summary>What Settings calls a pack, or its id if this build doesn't have it.</summary>
    public static string PackLabel(string id) => Packs.FirstOrDefault(p => p.Id == id)?.Label ?? id;

    public static bool IsBuiltInSchemeId(string? id) => Packs.Any(p => p.Id == id);

    public static bool IsUserSchemeId(string? id) =>
        id is not null && id.StartsWith(UserSchemePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>A new id for a scheme of the person's own.</summary>
    public static string NewUserSchemeId() => UserSchemePrefix + Guid.NewGuid().ToString("N");

    /// <summary>The longest scheme id that gets a folder of its own; a real one is 37 characters.</summary>
    private const int MaxSchemeFolderNameLength = 64;

    /// <summary>
    /// The name of a scheme's own folder of sound files, under the sounds folder (#948): its id, in
    /// lowercase, since an id is stable through a rename and a name can hold characters a folder
    /// can't. Null for an id that can't safely be a folder name: one from a newer QuickMail, or a
    /// hand edit such as <c>user-..\x</c>. A scheme with no folder has no files of its own, so its
    /// custom sounds play as missing.
    /// </summary>
    public static string? SchemeFolderName(string? schemeId)
    {
        if (IsBuiltInSchemeId(schemeId)) return schemeId;
        if (!IsUserSchemeId(schemeId) || schemeId!.Length > MaxSchemeFolderNameLength
            || schemeId.Length == UserSchemePrefix.Length) return null;
        foreach (var c in schemeId)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-')) return null;
        }
        return schemeId.ToLowerInvariant();
    }

    /// <summary>A built-in scheme as it ships, with nothing changed.</summary>
    public static SoundScheme BuiltInScheme(string packId) =>
        new(packId, PackLabel(packId), isBuiltIn: true, pack: packId);

    /// <summary>
    /// The scheme in use: the one config.ini names, with whatever is stored for it. An id this build
    /// doesn't know, from a newer QuickMail or a scheme since deleted, plays as Glass.
    /// </summary>
    public static SoundScheme ActiveScheme(ConfigModel config)
    {
        var id = config.SoundSchemeId;
        if (IsBuiltInSchemeId(id))
            return config.SoundSchemes.TryGetValue(id, out var stored) ? stored : BuiltInScheme(id);
        if (IsUserSchemeId(id) && config.SoundSchemes.TryGetValue(id, out var own))
            return own;
        return config.SoundSchemes.TryGetValue(DefaultPack, out var glass) ? glass : BuiltInScheme(DefaultPack);
    }

    /// <summary>
    /// What an event plays in a scheme. In a built-in scheme: the person's choice if a valid one is
    /// stored, else the event's built-in sound. In a scheme of the person's own: the stored choice,
    /// else nothing, since a new scheme starts silent. An unreadable stored value falls back the same way.
    /// </summary>
    public static SoundRef Resolve(SoundEvent e, SoundScheme scheme)
    {
        var info = Info(e);
        if (scheme.Events.TryGetValue(info.ConfigKey, out var value) && SoundRef.TryParse(value, out var chosen))
            return chosen;
        return scheme.IsBuiltIn ? SoundRef.BuiltIn(info.BuiltInSound) : SoundRef.None;
    }

    /// <summary>
    /// The pack a built-in sound plays in: its own if it names one, else the scheme's, else Glass,
    /// which is where a scheme of the person's own plays an unqualified built-in sound.
    /// </summary>
    public static string PackFor(SoundRef sound, SoundScheme? scheme) =>
        sound.Pack ?? scheme?.Pack ?? DefaultPack;
}
