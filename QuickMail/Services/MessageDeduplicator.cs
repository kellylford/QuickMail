using System;
using System.Collections.Generic;
using System.Linq;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Collapses duplicate copies of one physical message that appear across multiple folders in an
/// aggregate/virtual view (All Mail, per-account All Mail, saved views, and the conversation /
/// sender-group trees built from them).
///
/// Gmail exposes a single message in many IMAP folders at once — INBOX, [Gmail]/All Mail,
/// [Gmail]/Important, [Gmail]/Starred, and every user label — each with a *different* per-folder
/// UID (<see cref="MailMessageSummary.MessageId"/>). Identifying messages by UID alone therefore
/// shows the same mail repeatedly in any view that unions folders (issue #220). This collapses
/// those copies by the RFC 5322 Message-ID, which is identical across every copy.
///
/// Single real-folder views must NOT use this — a folder should show its own contents as-is.
/// </summary>
public static class MessageDeduplicator
{
    /// <summary>
    /// Normalizes an RFC 5322 Message-ID for use as a collapse key: trims, strips one pair of
    /// surrounding angle brackets, and lowercases invariantly. Returns empty for null/whitespace.
    /// </summary>
    public static string Normalize(string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return string.Empty;
        var s = messageId.Trim();
        if (s.Length >= 2 && s[0] == '<' && s[^1] == '>')
            s = s[1..^1].Trim();
        return s.ToLowerInvariant();
    }

    /// <summary>
    /// Returns one representative per unique message for an aggregate view, preserving the input's
    /// (typically newest-first) order. Copies are grouped by (account, normalized Message-ID);
    /// messages with no Message-ID are never merged (each is kept distinct on its per-folder key).
    /// The representative is the copy whose source folder is the most intuitive "home" — Inbox
    /// first, Gmail's virtual folders (All Mail / Important / Starred) last — so opening, reading,
    /// flagging, and deleting act on a real, predictable mailbox copy.
    /// </summary>
    /// <param name="messages">The combined message set for the aggregate view.</param>
    /// <param name="folderKind">
    /// Resolves a message's source folder to its <see cref="SpecialFolderKind"/>, used only to rank
    /// representatives. May return <see cref="SpecialFolderKind.None"/> for ordinary folders/labels.
    /// </param>
    public static List<MailMessageSummary> CollapseForAggregate(
        IEnumerable<MailMessageSummary> messages,
        Func<MailMessageSummary, SpecialFolderKind> folderKind)
    {
        // Winner per collapse key, plus a parallel list of keys in first-seen order so the output
        // preserves the caller's ordering.
        var winners = new Dictionary<string, MailMessageSummary>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var msg in messages)
        {
            var key = CollapseKeyFor(msg);
            if (winners.TryGetValue(key, out var current))
            {
                if (IsBetterRepresentative(msg, current, folderKind))
                    winners[key] = msg;
            }
            else
            {
                winners[key] = msg;
                order.Add(key);
            }
        }

        return order.Select(k => winners[k]).ToList();
    }

    /// <summary>
    /// The global-identity key used to collapse copies across folders in an aggregate view:
    /// (account, normalized Message-ID). Messages with no Message-ID fall back to their unique
    /// per-folder key so they are never merged with anything.
    ///
    /// Deliberate assumption: within one account, a non-empty Message-ID identifies one message.
    /// RFC 5322 requires Message-IDs to be globally unique, and Gmail's own dedup (X-GM-MSGID) is
    /// equivalent — so this mirrors the server. A misbehaving sender that reuses a Message-ID for
    /// two genuinely different messages would collapse them in aggregate views; the hidden one still
    /// exists in its real folder (no data loss), and this is preferred over showing the far more
    /// common Gmail label duplicates. Not keyed on Date/Subject on purpose: those can differ between
    /// copies of the same message and would reintroduce duplicates.
    /// </summary>
    public static string CollapseKeyFor(MailMessageSummary msg)
    {
        var id = Normalize(msg.InternetMessageId);
        // No Message-ID: fall back to the per-folder identity so it stays unique and un-mergeable.
        // The "u:" / "m:" prefixes keep the two key spaces from ever colliding.
        if (string.IsNullOrEmpty(id))
            return PerFolderKeyFor(msg);
        return "m:" + msg.AccountId + " " + id;
    }

    /// <summary>
    /// The per-folder identity key (account, folder, UID) — used by single real-folder views,
    /// which never collapse across folders.
    /// </summary>
    public static string PerFolderKeyFor(MailMessageSummary msg) =>
        "u:" + msg.AccountId + " " + msg.FolderName + " " + msg.MessageId;

    private static bool IsBetterRepresentative(
        MailMessageSummary candidate, MailMessageSummary current,
        Func<MailMessageSummary, SpecialFolderKind> folderKind)
    {
        var cp = HomePriority(folderKind(candidate));
        var pp = HomePriority(folderKind(current));
        if (cp != pp) return cp < pp;
        // Same priority: prefer the newer copy, then a stable ordinal tie-break on folder name.
        if (candidate.Date != current.Date) return candidate.Date > current.Date;
        return string.CompareOrdinal(candidate.FolderName, current.FolderName) < 0;
    }

    // Lower is a better "home" for the representative copy.
    private static int HomePriority(SpecialFolderKind kind) => kind switch
    {
        SpecialFolderKind.Inbox                            => 0,
        SpecialFolderKind.None                             => 1, // ordinary folder / user label
        SpecialFolderKind.Sent or SpecialFolderKind.Drafts => 2,
        _                                                  => 3, // AllMail, Important, Starred, Junk, Trash
    };
}
