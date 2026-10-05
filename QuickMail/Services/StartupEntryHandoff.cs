using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace QuickMail.Services;

/// <summary>A start-at-sign-in Run entry as it was before an uninstall removed it (#770).</summary>
/// <param name="Name">The Run value name ("QuickMail", or "QuickMail (xxxxxxxx)" for a --profileDir profile).</param>
/// <param name="Command">The Run value's command line.</param>
/// <param name="Approval">Windows' on/off mark under StartupApproved\Run, or null when there was none.</param>
public sealed record StartupEntry(string Name, string Command, byte[]? Approval);

/// <summary>
/// Carries start-at-sign-in entries across an MSI upgrade (#245, #770).
///
/// A Windows Installer major upgrade uninstalls the old copy before installing the new one, and
/// that uninstall runs QuickMail's uninstall hook, which removes the startup entries — correct for
/// a real uninstall, wrong for an upgrade, and nothing tells the hook which it is. So the uninstall
/// hook writes down what it removed, and the new copy's install hook, which Velopack runs straight
/// after the reinstall, puts it back. A hand-off left by a real uninstall is never consumed by an
/// upgrade; it is ignored once older than <see cref="MaxAge"/>, so reinstalling QuickMail weeks
/// later does not quietly turn start at sign-in back on.
///
/// Both hooks run as the user, in the same kind of Windows Installer custom-action process (or
/// both under Velopack's Update.exe), so they resolve the same temp folder.
/// </summary>
public static class StartupEntryHandoff
{
    /// <summary>An MSI upgrade takes seconds between the two hooks; this leaves room for a slow one.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

    public static string DefaultPath => Path.Combine(Path.GetTempPath(), "quickmail-startup-handoff.json");

    private sealed record Payload(DateTimeOffset SavedAt, List<StartupEntry> Entries);

    /// <summary>Records <paramref name="entries"/>; with none, clears any older hand-off instead.</summary>
    public static void Save(string path, IReadOnlyList<StartupEntry> entries, DateTimeOffset now)
    {
        if (entries.Count == 0)
        {
            File.Delete(path);
            return;
        }
        File.WriteAllText(path, JsonSerializer.Serialize(new Payload(now, new List<StartupEntry>(entries))));
    }

    /// <summary>
    /// Reads and deletes the hand-off. Returns nothing when there is none, when it is older than
    /// <see cref="MaxAge"/> (or dated in the future), or when it cannot be read — in every case the
    /// file is gone afterwards, so a bad or stale hand-off is never acted on.
    /// </summary>
    public static IReadOnlyList<StartupEntry> Take(string path, DateTimeOffset now)
    {
        if (!File.Exists(path)) return Array.Empty<StartupEntry>();
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(path));
            if (payload?.Entries is null) return Array.Empty<StartupEntry>();
            var age = now - payload.SavedAt;
            return age >= TimeSpan.Zero && age <= MaxAge ? payload.Entries : Array.Empty<StartupEntry>();
        }
        catch (JsonException)
        {
            return Array.Empty<StartupEntry>();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
