using System;
using System.IO;
using System.Text;

namespace QuickMail.Tests;

/// <summary>Small WAV files built in memory, for the sound scheme tests: a scheme file may hold only 16-bit PCM WAVs.</summary>
internal static class TestWav
{
    /// <summary>A short tone. <paramref name="salt"/> changes the samples, to make a different sound of the same shape.</summary>
    public static byte[] Tone(int bits = 16, int channels = 1, int rate = 8000, double seconds = 0.05,
        ushort formatTag = 1, byte salt = 0, int? declaredDataBytes = null)
    {
        var bytesPerSample = (bits + 7) / 8;
        var frames = (int)(rate * seconds);
        var data = new byte[frames * channels * bytesPerSample];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(i * 7 + salt);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII);
        w.Write("RIFF"u8);
        w.Write(36 + data.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write(formatTag);
        w.Write((ushort)channels);
        w.Write(rate);
        w.Write(rate * channels * bytesPerSample);
        w.Write((ushort)(channels * bytesPerSample));
        w.Write((ushort)bits);
        w.Write("data"u8);
        w.Write(declaredDataBytes ?? data.Length);
        w.Write(data);
        w.Flush();
        return ms.ToArray();
    }
}
