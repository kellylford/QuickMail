using System;
using System.IO;

namespace QuickMail.Services;

/// <summary>
/// Holds the resolved profile directory for this process invocation.
/// All services that write files derive their paths from this object.
/// </summary>
public sealed class ProfileContext
{
    public string ProfileDir { get; }

    public ProfileContext(string profileDir)
    {
        ProfileDir = profileDir;
        Directory.CreateDirectory(profileDir);
    }

    /// <summary>
    /// Returns a ProfileContext for the default location (%APPDATA%\QuickMail).
    /// Used when --profileDir is not supplied.
    /// </summary>
    public static ProfileContext Default() =>
        new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QuickMail"));

    /// <summary>
    /// Extracts the value following --profileDir from the argument list.
    /// Returns null if the flag is absent or has no following value.
    /// </summary>
    public static string? ParseProfileDir(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--profileDir", StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    /// <summary>
    /// Validates <paramref name="rawDir"/>, creates it if needed, and returns a
    /// <see cref="ProfileContext"/>. Returns null and sets <paramref name="error"/>
    /// to a human-readable message if the path cannot be used.
    /// </summary>
    public static ProfileContext? TryCreate(string rawDir, out string? error)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(rawDir);
        }
        catch (Exception ex)
        {
            error = $"The path is not valid: {ex.Message}";
            return null;
        }

        if (File.Exists(fullPath))
        {
            error = "The path exists as a file, not a directory.";
            return null;
        }

        try
        {
            Directory.CreateDirectory(fullPath);
        }
        catch (Exception ex)
        {
            error = $"Could not create the directory: {ex.Message}";
            return null;
        }

        var probe = Path.Combine(fullPath, $".qm-write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            error = $"The directory is not writable: {ex.Message}";
            return null;
        }

        error = null;
        return new ProfileContext(fullPath);
    }
}
