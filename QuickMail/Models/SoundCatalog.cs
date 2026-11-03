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
/// <param name="OnByDefault">Whether it plays once event sounds are turned on. Off for actions that repeat quickly.</param>
/// <param name="Origin">Why it plays.</param>
public sealed record SoundEventInfo(
    SoundEvent Event, string ConfigKey, string Label, string BuiltInSound, bool OnByDefault, SoundOrigin Origin);

/// <summary>Every sound event and every built-in sound.</summary>
public static class SoundCatalog
{
    /// <summary>The sound a new rule starts with.</summary>
    public const string DefaultRuleSound = "matched";

    /// <summary>The one built-in pack in this release.</summary>
    public const string DefaultPack = "quickmail";

    public static readonly IReadOnlyList<SoundEventInfo> Events =
    [
        new(SoundEvent.MessageSent,        "MessageSent",        "Message sent",               "sent",         true,  SoundOrigin.Direct),
        new(SoundEvent.MessageQueued,      "MessageQueued",      "Message queued",             "queued",       true,  SoundOrigin.Direct),
        new(SoundEvent.SendFailed,         "SendFailed",         "Send failed",                "failed",       true,  SoundOrigin.Direct),
        new(SoundEvent.NewMail,            "NewMail",            "New mail",                   "arrived",      true,  SoundOrigin.Arrival),
        new(SoundEvent.WatchedMail,        "WatchedMail",        "Watched conversation mail",  "attention",    true,  SoundOrigin.Arrival),
        new(SoundEvent.ActionFailed,       "ActionFailed",       "Delete, move or archive failed", "error",    true,  SoundOrigin.Direct),
        new(SoundEvent.ConnectionLost,     "ConnectionLost",     "Connection lost",            "disconnected", true,  SoundOrigin.Background),
        new(SoundEvent.ConnectionRestored, "ConnectionRestored", "Connection restored",        "connected",    true,  SoundOrigin.Background),
        new(SoundEvent.CalendarReminder,   "CalendarReminder",   "Calendar reminder",          "reminder",     true,  SoundOrigin.Background),
        new(SoundEvent.MessageDeleted,     "MessageDeleted",     "Message deleted",            "removed",      false, SoundOrigin.Direct),
        new(SoundEvent.MessageArchived,    "MessageArchived",    "Message archived",           "filed",        false, SoundOrigin.Direct),
        new(SoundEvent.MessageMoved,       "MessageMoved",       "Message moved",              "moved",        false, SoundOrigin.Direct),
    ];

    /// <summary>Every built-in sound name: one per event, plus the rule default.</summary>
    public static readonly IReadOnlyList<string> BuiltInSounds =
        [.. Events.Select(e => e.BuiltInSound), DefaultRuleSound];

    private static readonly Dictionary<SoundEvent, SoundEventInfo> ByEvent = Events.ToDictionary(e => e.Event);

    public static SoundEventInfo Info(SoundEvent e) => ByEvent[e];

    /// <summary>
    /// What an event plays under these settings: the person's choice if a valid one is stored, else
    /// its built-in sound if it is on by default, else nothing. An unreadable stored value falls back
    /// the same way rather than silencing the event.
    /// </summary>
    public static SoundRef Resolve(SoundEvent e, IReadOnlyDictionary<string, string> stored)
    {
        var info = Info(e);
        if (stored.TryGetValue(info.ConfigKey, out var value) && SoundRef.TryParse(value, out var chosen))
            return chosen;
        return info.OnByDefault ? SoundRef.BuiltIn(info.BuiltInSound) : SoundRef.None;
    }
}
