using System;
using System.Collections.Generic;

namespace QuickMail.Models;

/// <summary>
/// A set of choices of sound, one per event (#932). The built-in schemes (Glass, Soft bell, Wood)
/// store only what the person changed, so a later change to a shipped default still reaches anyone
/// who never touched that event. A scheme of the person's own stores every event, and an event it
/// doesn't list plays nothing: a new scheme starts silent. A screen reader reads the item from
/// <see cref="ToString"/>.
/// </summary>
public sealed class SoundScheme
{
    public SoundScheme(string id, string name, bool isBuiltIn, string? pack = null,
        IEnumerable<KeyValuePair<string, string>>? events = null, string? author = null, string? license = null)
    {
        Id = id;
        Name = name;
        IsBuiltIn = isBuiltIn;
        Pack = pack;
        Author = string.IsNullOrWhiteSpace(author) ? null : author;
        License = string.IsNullOrWhiteSpace(license) ? null : license;
        Events = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in events ?? []) Events[key] = value;
    }

    /// <summary>Its section in config.ini is <c>[sounds.&lt;Id&gt;]</c>; a built-in scheme's id is its pack.</summary>
    public string Id { get; }

    /// <summary>What Settings calls it.</summary>
    public string Name { get; }

    public bool IsBuiltIn { get; }

    /// <summary>The folder of built-in WAVs a built-in scheme plays in; null for a scheme of the person's own.</summary>
    public string? Pack { get; }

    /// <summary>Who made it, for a scheme that came from someone else (#937). Kept so that sharing it on keeps the credit.</summary>
    public string? Author { get; }

    /// <summary>The licence it came under, such as CC-BY-4.0, which may require crediting <see cref="Author"/>.</summary>
    public string? License { get; }

    /// <summary>
    /// An event's <see cref="SoundEventInfo.ConfigKey"/> to the <see cref="SoundRef"/> stored for it, as
    /// written. Kept as written, so an entry from a newer QuickMail survives; <see cref="SoundCatalog.Resolve(SoundEvent, SoundScheme)"/>
    /// checks a value when it is used.
    /// </summary>
    public Dictionary<string, string> Events { get; }

    /// <summary>A copy that can be changed without touching this one, which the sound worker may be reading.</summary>
    public SoundScheme With(string? name = null, IEnumerable<KeyValuePair<string, string>>? events = null) =>
        new(Id, name ?? Name, IsBuiltIn, Pack, events ?? Events, Author, License);

    public override string ToString() => Name;
}
