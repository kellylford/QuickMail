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

/// <summary>A style of built-in sound. A screen reader reads the item from <see cref="ToString"/>.</summary>
/// <param name="Id">Its folder under Sounds/BuiltIn, and its value in config.ini.</param>
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

    /// <summary>The style used until someone chooses another, or if config.ini names one this build doesn't have.</summary>
    public const string DefaultPack = "glass";

    /// <summary>Every style has every sound, with the same melody; only the tone differs.</summary>
    public static readonly IReadOnlyList<SoundPack> Packs =
    [
        new("glass", "Glass"),
        new("softbell", "Soft bell"),
        new("wood", "Wood"),
    ];

    /// <summary>The folder under the profile that holds custom sounds.</summary>
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
    ];

    /// <summary>Every built-in sound name: one per event, plus the rule default.</summary>
    public static readonly IReadOnlyList<string> BuiltInSounds =
        [.. Events.Select(e => e.BuiltInSound), DefaultRuleSound];

    private static readonly Dictionary<SoundEvent, SoundEventInfo> ByEvent = Events.ToDictionary(e => e.Event);

    public static SoundEventInfo Info(SoundEvent e) => ByEvent[e];

    /// <summary>Identifies a message between the rule that sounded for it and the new-mail sound.</summary>
    public static string MessageKey(Guid accountId, string messageId) => $"{accountId:N}|{messageId}";

    /// <summary>What Settings calls a built-in sound: "arrived" is "Arrived".</summary>
    public static string BuiltInLabel(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

    /// <summary>
    /// What an event plays under these settings: the person's choice if a valid one is stored, else
    /// its built-in sound. An unreadable stored value falls back
    /// the same way rather than silencing the event.
    /// </summary>
    public static SoundRef Resolve(SoundEvent e, IReadOnlyDictionary<string, string> stored)
    {
        var info = Info(e);
        if (stored.TryGetValue(info.ConfigKey, out var value) && SoundRef.TryParse(value, out var chosen))
            return chosen;
        return SoundRef.BuiltIn(info.BuiltInSound);
    }
}
