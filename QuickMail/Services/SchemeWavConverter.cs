using System;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Turns a sound QuickMail plays, but a scheme file can't hold (an MP3, a 24-bit or
/// WAVE_FORMAT_EXTENSIBLE WAV), into the 16-bit PCM WAV every app reads alike, so Export scheme can
/// put it in the file instead of leaving it out (app-kit sounds/SCHEMES.md, "Sounds in a scheme file").
/// </summary>
public static class SchemeWavConverter
{
    /// <summary>The rate a sound is resampled to when its own is outside what a scheme file allows.</summary>
    private const int FallbackRate = 44100;

    /// <summary>
    /// The sound at <paramref name="path"/> as a 16-bit PCM WAV: its own sample rate if that's 8 to
    /// 192 kHz, else 44.1 kHz; mono or stereo as it was, more channels mixed down to stereo. Null if it
    /// can't be decoded, or the result breaks a scheme file's limits (5 MB, 30 seconds).
    /// </summary>
    public static byte[]? Convert(string path)
    {
        try
        {
            // The same decoding as playing it, so what converts is what QuickMail would have played.
            using var reader = NAudioSoundOutput.Open(new SoundClip(path, path, null));
            ISampleProvider samples = reader.ToSampleProvider();
            if (samples.WaveFormat.Channels > 2) samples = new DownmixToStereo(samples);
            if (samples.WaveFormat.SampleRate is < 8000 or > 192000)
                samples = new WdlResamplingSampleProvider(samples, FallbackRate);

            using var ms = new MemoryStream();
            // WaveFileWriter leaves a stream it was given open, so ms is still readable afterwards.
            using (var writer = new WaveFileWriter(ms,
                       new WaveFormat(samples.WaveFormat.SampleRate, 16, samples.WaveFormat.Channels)))
            {
                var pcm = new SampleToWaveProvider16(samples);
                var buffer = new byte[64 * 1024];
                for (int read; (read = pcm.Read(buffer.AsSpan())) > 0;)
                {
                    writer.Write(buffer, 0, read);
                    if (ms.Length > SoundCatalog.MaxCustomFileBytes) return null;
                }
            }
            var bytes = ms.ToArray();
            if (SoundSchemeFile.SchemeWavProblem(bytes) is { } problem)
            {
                LogService.Log($"Sound scheme export: {Path.GetFileName(path)} converted, but can't go in a scheme file: {problem}");
                return null;
            }
            return bytes;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any decoder failure (a damaged file, no MP3 decoder on this PC) leaves the sound out, as before.
            LogService.Log($"Sound scheme export: could not convert {Path.GetFileName(path)} to WAV", ex);
            return null;
        }
    }

    /// <summary>More than two channels mixed to stereo: the even channels' average on the left, the odd channels' on the right.</summary>
    private sealed class DownmixToStereo(ISampleProvider source) : ISampleProvider
    {
        private readonly int _channels = source.WaveFormat.Channels;
        private float[] _buffer = [];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);

        public int Read(Span<float> buffer)
        {
            var frames = buffer.Length / 2;
            var needed = frames * _channels;
            if (_buffer.Length < needed) _buffer = new float[needed];
            var read = source.Read(_buffer.AsSpan(0, needed)) / _channels;
            for (var f = 0; f < read; f++)
            {
                float left = 0, right = 0;
                int lefts = 0, rights = 0;
                for (var c = 0; c < _channels; c++)
                {
                    var sample = _buffer[f * _channels + c];
                    if (c % 2 == 0) { left += sample; lefts++; }
                    else { right += sample; rights++; }
                }
                buffer[f * 2] = left / lefts;
                buffer[f * 2 + 1] = right / rights;
            }
            return read * 2;
        }
    }
}
