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
    /// <param name="storedSound">The sound, as stored.</param>
    /// <param name="messageKeys">The messages this sound is for (<see cref="SoundCatalog.MessageKey"/>), so
    /// that they don't also sound as new mail, however much later the sync gets round to that.</param>
    void PlayRule(string? storedSound, IEnumerable<string>? messageKeys = null);

    /// <summary>Whether a rule has already played a sound for this message.</summary>
    bool WasSoundedByRule(string messageKey);

    /// <summary>
    /// Plays a sound because the person asked to hear it, so even when muted. Replaces anything
    /// playing, and previewing no sound just stops it.
    /// </summary>
    /// <param name="storedSound">The sound, as stored.</param>
    /// <param name="volumePercent">A volume to hear it at, for one not saved yet.</param>
    /// <param name="pack">The pack to hear a built-in sound with no pack of its own in, for a scheme not saved yet.</param>
    /// <param name="schemeId">The scheme whose folder a custom sound is in; null for a rule's own sound (#948).</param>
    /// <param name="fallback">The built-in sound to play if a custom file is gone or won't play; null for the default rule sound.</param>
    void Preview(string storedSound, int? volumePercent = null, string? pack = null, string? schemeId = null, string? fallback = null);

    /// <summary>
    /// Previews what a rule with this sound would play now: for the default sound, the scheme in use's
    /// "A rule matched", which may be nothing (#932). Otherwise as <see cref="Preview"/>.
    /// </summary>
    void PreviewRule(string? storedSound);

    /// <summary>
    /// Plays what an event would play now, in the scheme in use and at the saved volume, because the
    /// person asked to hear it: so even with event sounds off or muted. Replaces anything playing, and
    /// an event with no sound just stops it. A custom file that's gone or won't play falls back to the
    /// event's own built-in sound, as when the event really happens.
    /// </summary>
    void PreviewEvent(SoundEvent soundEvent);

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

    // Every custom sound file lives in one folder (#948): a scheme's own, which a scheme's events play
    // from and which is deleted with the scheme, or, for a rule's own sound, the sounds folder itself.
    // In each method below, schemeId names the scheme, and null means the rules' folder.

    /// <summary>The file names in a scheme's folder, or with a null id, the rules' folder.</summary>
    IReadOnlyList<string> CustomSoundFiles(string? schemeId);

    /// <summary>The full path of a custom sound in a scheme's folder (null: the rules'), or null if there is none by that name.</summary>
    string? CustomSoundPath(string? schemeId, string fileName);

    /// <summary>
    /// Deletes a custom sound from a scheme's folder, or with a null id, the rules' folder (#932).
    /// Anything still naming it then plays its fallback, as for a file that has gone missing. True if
    /// the file is gone, including if it was never there.
    /// </summary>
    /// <param name="schemeId">The scheme, or null for the rules' folder.</param>
    /// <param name="fileName">The file's name in the folder.</param>
    /// <param name="refusal">Why it couldn't be deleted, in words for the person.</param>
    bool TryRemoveCustomSound(string? schemeId, string fileName, out string refusal);

    /// <summary>
    /// Checks a file the person chose and copies it into a scheme's folder, or with a null id, the
    /// rules' folder, so it survives the original moving and travels in settings files.
    /// </summary>
    /// <param name="schemeId">The scheme, or null for the rules' folder.</param>
    /// <param name="sourcePath">The chosen file.</param>
    /// <param name="fileName">Its name in the folder, which can differ from the original's.</param>
    /// <param name="refusal">Why it was refused, in words for the person.</param>
    bool TryAddCustomSound(string? schemeId, string sourcePath, out string fileName, out string refusal);

    /// <summary>
    /// Copies every sound file in one scheme's folder into another's, for a copy of a scheme (#948).
    /// A name the other folder already has keeps its own file. False, after logging, if any file
    /// could not be copied.
    /// </summary>
    bool TryCopySchemeSounds(string fromSchemeId, string toSchemeId);

    /// <summary>
    /// Deletes a scheme's folder and every file in it, for a scheme that has been deleted (#948). No
    /// other scheme's files, and none of the rules', are touched. True if the folder is gone,
    /// including if there never was one.
    /// </summary>
    bool TryDeleteSchemeSounds(string schemeId);

    /// <summary>
    /// Writes down the folders of schemes made but not saved yet (#955), so that if they are never
    /// saved, even because QuickMail stops, <see cref="RemoveUnusedSchemeFolders"/> can remove them.
    /// Call before anything goes in the folder.
    /// </summary>
    void NoteUnsavedSchemeFolders(IEnumerable<string> schemeIds);

    /// <summary>Crosses off schemes written down by <see cref="NoteUnsavedSchemeFolders"/> that are now saved, or whose folders are gone.</summary>
    void ForgetUnsavedSchemeFolders(IEnumerable<string> schemeIds);

    /// <summary>
    /// Deletes the folders written down as unsaved (<see cref="NoteUnsavedSchemeFolders"/>) that
    /// nothing owns (#955): not named in config.ini or an import backup's, and not in
    /// <paramref name="schemesInUse"/>. No other folder is ever touched, and nothing is removed if it
    /// can't be sure what config.ini owns (<see cref="SoundSchemeFolderCleanup"/>).
    /// </summary>
    /// <param name="schemesInUse">Schemes whose folders stay whatever config.ini says, such as every scheme open in Settings.</param>
    /// <returns>How many folders were deleted, or null if it removed nothing because it couldn't be sure.</returns>
    int? RemoveUnusedSchemeFolders(IEnumerable<string> schemesInUse);
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
