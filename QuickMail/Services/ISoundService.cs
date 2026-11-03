using System;
using System.IO;
using System.Threading;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Plays QuickMail's sounds (issue #912). Every method returns at once, can be called from any thread,
/// and never throws: a sound is a courtesy, and no audio problem may delay or break mail. A sound
/// never replaces an announcement or status text.
/// </summary>
public interface ISoundService : IDisposable
{
    /// <summary>Plays an event's sound, if event sounds are on and the event has one.</summary>
    void Play(SoundEvent soundEvent);

    /// <summary>
    /// Plays a rule's sound, given as stored in <see cref="MailRule"/>. Does not depend on event sounds
    /// being on, since adding a sound to a rule already asks for it, but mute still silences it. A
    /// missing or unreadable value plays the default rule sound rather than nothing.
    /// </summary>
    void PlayRule(string? storedSound);

    /// <summary>Plays a sound because the person asked to hear it, so even when muted. Replaces anything playing.</summary>
    void Preview(string storedSound);

    /// <summary>
    /// Whether this event would make a sound under the current settings, so its toast can be silent
    /// rather than sounding twice.
    /// </summary>
    bool IsAudible(SoundEvent soundEvent);
}

/// <summary>A sound ready to play: a file on disk, or a stream from the app's own resources.</summary>
/// <param name="Key">Identifies the sound, for repeat detection and logging.</param>
/// <param name="FilePath">The file, for a custom sound.</param>
/// <param name="OpenStream">Opens a built-in WAV.</param>
public sealed record SoundClip(string Key, string? FilePath, Func<Stream>? OpenStream);

/// <summary>The audio engine, kept behind this so tests use a fake and the engine can be swapped.</summary>
public interface ISoundOutput
{
    /// <summary>Plays the clip on the default device and returns when it ends. Throws on any failure.</summary>
    void Play(SoundClip clip, float volume, CancellationToken cancellationToken);
}
