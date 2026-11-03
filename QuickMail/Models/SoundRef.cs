using System;
using System.IO;
using System.Linq;

namespace QuickMail.Models;

public enum SoundRefKind
{
    None,
    BuiltIn,
    Custom,
}

/// <summary>
/// A chosen sound as stored in config.ini and rules.json: <c>none</c>, <c>builtin:&lt;name&gt;</c> or
/// <c>custom:&lt;file&gt;</c>. A custom sound is only ever a bare file name inside the profile's sounds
/// folder, because these values arrive in settings files from elsewhere: a stored path must not be able
/// to point anywhere else on disk.
/// </summary>
public readonly record struct SoundRef(SoundRefKind Kind, string Name)
{
    private const string BuiltInPrefix = "builtin:";
    private const string CustomPrefix = "custom:";
    private const string NoneValue = "none";

    public static SoundRef None => new(SoundRefKind.None, string.Empty);

    public static SoundRef BuiltIn(string name) => new(SoundRefKind.BuiltIn, name);

    public static SoundRef Custom(string fileName) => new(SoundRefKind.Custom, fileName);

    /// <summary>The form stored in config.ini and rules.json.</summary>
    public string ToStored() => Kind switch
    {
        SoundRefKind.BuiltIn => BuiltInPrefix + Name,
        SoundRefKind.Custom => CustomPrefix + Name,
        _ => NoneValue,
    };

    public static bool TryParse(string? value, out SoundRef sound)
    {
        sound = None;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();

        if (text.Equals(NoneValue, StringComparison.OrdinalIgnoreCase)) return true;

        if (text.StartsWith(BuiltInPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = text[BuiltInPrefix.Length..];
            if (!SoundCatalog.BuiltInSounds.Contains(name, StringComparer.Ordinal)) return false;
            sound = BuiltIn(name);
            return true;
        }

        if (text.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var file = text[CustomPrefix.Length..];
            if (!IsSafeCustomFileName(file)) return false;
            sound = Custom(file);
            return true;
        }

        return false;
    }

    /// <summary>A bare WAV or MP3 file name, with nothing that could lead out of the sounds folder.</summary>
    public static bool IsSafeCustomFileName(string? file)
    {
        if (string.IsNullOrWhiteSpace(file)) return false;
        if (file != file.Trim() || file.EndsWith('.')) return false;   // Windows drops these, changing which file is meant.
        if (file.IndexOfAny(['/', '\\', ':']) >= 0) return false;
        if (file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        var extension = Path.GetExtension(file);
        return extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase);
    }
}
