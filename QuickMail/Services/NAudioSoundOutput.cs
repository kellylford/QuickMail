using System;
using System.IO;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

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

    /// <summary>Longer than any sound can be (import caps a custom file at 30 seconds), so a stuck driver can't hold the queue forever.</summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(40);

    public void Play(SoundClip clip, float volume, CancellationToken cancellationToken)
    {
        using var reader = Open(clip);
        // Scaled here, not on the player: the player's volume is the session's, which shows in the
        // Windows volume mixer and would be remembered for this app there.
        var scaled = new VolumeSampleProvider(reader.ToSampleProvider()) { Volume = volume };
        var withLeadOut = new OffsetSampleProvider(scaled) { LeadOut = LeadOut };

        using var player = new WasapiPlayerBuilder().WithLatency(LatencyMilliseconds).Build();
        player.Init(withLeadOut.ToWaveProvider());

        using var stopped = new ManualResetEventSlim();
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

    /// <summary>Opens and decodes the clip. Throws on anything that isn't playable audio.</summary>
    internal static WaveStream Open(SoundClip clip)
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
