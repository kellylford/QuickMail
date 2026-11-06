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
/// A chosen sound as stored in config.ini and rules.json: <c>none</c>, <c>builtin:&lt;name&gt;</c>,
/// <c>builtin:&lt;pack&gt;/&lt;name&gt;</c> or <c>custom:&lt;file&gt;</c>. A built-in sound with no pack plays in the
/// current scheme's pack (Glass for a scheme of the person's own); one with a pack always plays in that
/// pack (#932). A custom sound is only ever a bare file name inside the profile's sounds
/// folder, because these values arrive in settings files from elsewhere: a stored path must not be able
/// to point anywhere else on disk.
/// </summary>
/// <param name="Kind">What sort of sound.</param>
/// <param name="Name">The built-in sound's name, or the custom file's name.</param>
/// <param name="Pack">For a built-in sound, the pack it always plays in; null for the scheme's own.</param>
public readonly record struct SoundRef(SoundRefKind Kind, string Name, string? Pack = null)
{
    private const string BuiltInPrefix = "builtin:";
    private const string CustomPrefix = "custom:";
    private const string NoneValue = "none";

    public static SoundRef None => new(SoundRefKind.None, string.Empty);

    public static SoundRef BuiltIn(string name, string? pack = null) => new(SoundRefKind.BuiltIn, name, pack);

    public static SoundRef Custom(string fileName) => new(SoundRefKind.Custom, fileName);

    /// <summary>What to call the sound on screen and in speech.</summary>
    public string DisplayName => Kind switch
    {
        SoundRefKind.BuiltIn when Pack is not null => $"{SoundCatalog.BuiltInLabel(Name)} ({SoundCatalog.PackLabel(Pack)})",
        SoundRefKind.BuiltIn => SoundCatalog.BuiltInLabel(Name),
        SoundRefKind.Custom => Name,
        _ => "None",
    };

    /// <summary>The form stored in config.ini and rules.json.</summary>
    public string ToStored() => Kind switch
    {
        SoundRefKind.BuiltIn when Pack is not null => BuiltInPrefix + Pack + "/" + Name,
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
            string? pack = null;
            var slash = name.IndexOf('/');
            if (slash >= 0)
            {
                pack = name[..slash];
                name = name[(slash + 1)..];
                // An unknown pack is refused like an unknown name: it may be from a newer QuickMail.
                if (!SoundCatalog.Packs.Any(p => p.Id == pack)) return false;
            }
            if (!SoundCatalog.BuiltInSounds.Contains(name, StringComparer.Ordinal)) return false;
            sound = BuiltIn(name, pack);
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

    // Leaves room for the profile path, so a long name can't fail mid-import and roll the whole thing back.
    private const int MaxFileNameLength = 100;

    // On older Windows, COM1.wav still means the serial port, whatever folder it is in.
    private static bool IsReservedDeviceName(string stem)
    {
        var name = stem.TrimEnd(' ', '.').ToUpperInvariant();
        if (name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$") return true;
        return name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal))
            && (char.IsDigit(name[3]) || name[3] is '¹' or '²' or '³');
    }

    /// <summary>A bare WAV or MP3 file name, with nothing that could lead out of the sounds folder or reach a device.</summary>
    public static bool IsSafeCustomFileName(string? file)
    {
        if (string.IsNullOrWhiteSpace(file)) return false;
        if (file != file.Trim() || file.EndsWith('.')) return false;   // Windows drops these, changing which file is meant.
        if (file.IndexOfAny(['/', '\\', ':']) >= 0) return false;
        if (file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        if (file.Length > MaxFileNameLength) return false;
        if (IsReservedDeviceName(Path.GetFileNameWithoutExtension(file))) return false;
        var extension = Path.GetExtension(file);
        return extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase);
    }
}
