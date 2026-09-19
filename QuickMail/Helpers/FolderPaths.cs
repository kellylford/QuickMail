using System;
using System.Collections.Generic;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Helpers;

/// <summary>Human-readable folder paths — never a raw Graph folder id.</summary>
public static class FolderPaths
{
    /// <summary>
    /// Human-readable folder path for display. IMAP keeps its separator-delimited FullName; Graph
    /// (whose FullName is an opaque id) is reconstructed from DisplayNames up the ParentId chain,
    /// e.g. "Inbox/Projects/2026", so no one is ever shown a raw folder id.
    /// </summary>
    public static string Build(MailFolderModel folder, IReadOnlyDictionary<string, MailFolderModel> byId)
    {
        if (folder.ParentId == null)
            return string.IsNullOrWhiteSpace(folder.FullName) ? folder.DisplayName : folder.FullName;

        var segments = new List<string> { folder.DisplayName };
        var current = folder;
        int guard = 0;
        while (current.ParentId != null && byId.TryGetValue(current.ParentId, out var parent) && guard < 64)
        {
            segments.Add(parent.DisplayName);
            current = parent;
            guard++;
        }
        // The guard only trips on a ParentId cycle (which Graph shouldn't produce). Surface it in
        // /debug so a subtly-truncated path is discoverable rather than silent.
        if (guard >= 64)
            LogService.Debug($"FolderPaths: path for '{folder.DisplayName}' hit the 64-deep guard — possible ParentId cycle.");
        segments.Reverse();
        return string.Join('/', segments);
    }

    /// <summary>
    /// The path of the folder named <paramref name="fullName"/> among <paramref name="folders"/>, with
    /// IMAP's all-capitals INBOX written as a person would ("Inbox/Receipts"). Falls back to the name
    /// itself when the folder is not in the list — except that an opaque id is never returned when
    /// a display name is known.
    /// </summary>
    public static string Describe(string fullName, IReadOnlyList<MailFolderModel>? folders, string? fallbackDisplayName = null)
    {
        if (folders is { Count: > 0 })
        {
            var byId = new Dictionary<string, MailFolderModel>(StringComparer.Ordinal);
            foreach (var f in folders) byId.TryAdd(f.FullName, f);
            if (byId.TryGetValue(fullName, out var folder))
                return FriendlyInbox(Build(folder, byId));
        }
        return FriendlyInbox(string.IsNullOrWhiteSpace(fallbackDisplayName) ? fullName : fallbackDisplayName);
    }

    private static string FriendlyInbox(string path)
    {
        if (path.Equals("INBOX", StringComparison.Ordinal)) return "Inbox";
        if (path.Length > 6 && path.StartsWith("INBOX", StringComparison.Ordinal) && !char.IsLetterOrDigit(path[5]))
            return "Inbox" + path[5..];
        return path;
    }
}
