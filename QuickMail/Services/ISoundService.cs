using System;
using System.Collections.Generic;
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
    /// <param name="soundEvent">What happened.</param>
    /// <param name="background">It happened on its own this time, so it stays quiet in a presentation
    /// even for an event that usually answers something the person just did.</param>
    void Play(SoundEvent soundEvent, bool background = false);

    /// <summary>
    /// Plays a rule's sound, given as stored in <see cref="MailRule"/>. Does not depend on event sounds
    /// being on, since adding a sound to a rule already asks for it, but mute still silences it. A
    /// missing or unreadable value plays the default rule sound rather than nothing.
    /// </summary>
    void PlayRule(string? storedSound);

    /// <summary>
    /// Plays a sound because the person asked to hear it, so even when muted. Replaces anything
    /// playing, and previewing no sound just stops it.
    /// </summary>
    /// <param name="storedSound">The sound, as stored.</param>
    /// <param name="volumePercent">A volume to hear it at, for one not saved yet.</param>
    void Preview(string storedSound, int? volumePercent = null);

    /// <summary>Stops what is playing and whatever is waiting.</summary>
    void StopAll();

    /// <summary>
    /// Whether toasts should leave out the Windows notification sound. True whenever event sounds are
    /// on or everything is muted, whatever each event is set to: someone who has muted sounds or set
    /// an event to None should not then hear Windows play one. With neither on, toasts are untouched.
    /// </summary>
    bool SilencesToasts { get; }

    /// <summary>Whether this event would make a sound under the current settings.</summary>
    bool IsAudible(SoundEvent soundEvent);

    /// <summary>The file names in the profile's folder of custom sounds.</summary>
    IReadOnlyList<string> CustomSoundFiles();

    /// <summary>
    /// Checks a file the person chose and copies it into the folder of custom sounds, so it survives
    /// the original moving and travels in settings files.
    /// </summary>
    /// <param name="sourcePath">The chosen file.</param>
    /// <param name="fileName">Its name in the folder, which can differ from the original's.</param>
    /// <param name="refusal">Why it was refused, in words for the person.</param>
    bool TryAddCustomSound(string sourcePath, out string fileName, out string refusal);
}

/// <summary>A sound ready to play: a file on disk, or a stream from the app's own resources.</summary>
/// <param name="Key">Identifies the sound, for repeat detection and logging.</param>
/// <param name="FilePath">The file, for a custom sound.</param>
/// <param name="OpenStream">Opens a built-in WAV.</param>
public sealed record SoundClip(string Key, string? FilePath, Func<Stream>? OpenStream);

/// <summary>A sound that runs longer than a sound may.</summary>
public sealed class SoundTooLongException(string message) : Exception(message);

/// <summary>The audio engine, kept behind this so tests use a fake and the engine can be swapped.</summary>
public interface ISoundOutput
{
    /// <summary>Plays the clip on the default device and returns when it ends. Throws on any failure.</summary>
    void Play(SoundClip clip, float volume, CancellationToken cancellationToken);

    /// <summary>Throws unless the file decodes as a sound short enough to play.</summary>
    void Check(string filePath);
}
