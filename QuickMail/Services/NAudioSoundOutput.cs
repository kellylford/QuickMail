using System;
using System.IO;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Plays a sound on the Windows default output with NAudio. A player is opened per sound, not held
/// open, so it follows the default device and doesn't keep the audio device awake between sounds.
/// See "What the spike measured" in docs/planning/sounds-pm-dev-spec.md.
/// </summary>
public sealed class NAudioSoundOutput : ISoundOutput
{
    /// <summary>
    /// Silence played after every sound. The player reports it has stopped while audio is still in
    /// its buffer, so without this about the last 50 ms of a sound was lost when the player closed,
    /// which can end it in a click. Longer than <see cref="LatencyMilliseconds"/> so the sound clears the buffer.
    /// </summary>
    private static readonly TimeSpan LeadOut = TimeSpan.FromMilliseconds(200);

    private const int LatencyMilliseconds = 100;

    /// <summary>Longer than <see cref="Open"/> lets any sound be, so a driver that never reports the end can't hold the queue forever.</summary>
    private static readonly TimeSpan MaxWait = SoundCatalog.MaxCustomSoundLength + TimeSpan.FromSeconds(10);

    public void Play(SoundClip clip, float volume, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stopped = new ManualResetEventSlim();   // Before the player: it outlives anything the player can still raise.
        using var reader = Open(clip);
        // Scaled here, not on the player: the player's volume is the session's, which shows in the
        // Windows volume mixer and would be remembered for this app there.
        var scaled = new VolumeSampleProvider(reader.ToSampleProvider()) { Volume = volume };
        var withLeadOut = new OffsetSampleProvider(scaled) { LeadOut = LeadOut };

        using var player = new WasapiPlayerBuilder().WithLatency(LatencyMilliseconds).Build();
        player.Init(withLeadOut.ToWaveProvider());

        Exception? failure = null;
        player.PlaybackStopped += (_, e) =>
        {
            failure = e.Exception;
            stopped.Set();
        };

        player.Play();
        try
        {
            if (!stopped.Wait(MaxWait, cancellationToken))
                throw new TimeoutException($"Sound {clip.Key} did not finish.");
        }
        catch (OperationCanceledException)
        {
            player.Stop();
            throw;
        }

        if (failure is not null) throw new IOException($"Playback of {clip.Key} failed.", failure);
    }

    /// <summary>
    /// Opens and decodes the clip. Throws on anything that isn't playable audio, or that runs longer
    /// than a sound may. Checked here because this is the one path every sound takes: a settings file
    /// or a file copied into the folder by hand never passes through Choose File, and a small file
    /// at a low bit rate can run for many minutes.
    /// </summary>
    internal static WaveStream Open(SoundClip clip)
    {
        var reader = OpenReader(clip);
        if (reader.TotalTime <= SoundCatalog.MaxCustomSoundLength) return reader;
        reader.Dispose();
        throw new InvalidDataException($"Sound {clip.Key} is longer than {SoundCatalog.MaxCustomSoundLength.TotalSeconds:0} seconds.");
    }

    private static WaveStream OpenReader(SoundClip clip)
    {
        if (clip.FilePath is { } path)
        {
            return Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase)
                ? new MediaFoundationReader(path)
                : new WaveFileReader(path);
        }

        if (clip.OpenStream is null) throw new InvalidOperationException($"Sound {clip.Key} has no source.");
        return new WaveFileReader(clip.OpenStream());
    }
}
