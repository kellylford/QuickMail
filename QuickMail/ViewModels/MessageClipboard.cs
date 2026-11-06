using System;
using System.Collections.Generic;
using System.Linq;
using QuickMail.Models;

namespace QuickMail.ViewModels;

/// <summary>What a paste would do, decided before anything touches a server.</summary>
public enum MessagePasteKind
{
    /// <summary>Nothing has been copied or cut.</summary>
    Nothing,

    /// <summary>The paste target is not a folder a message can be put in: a virtual folder such
    /// as All Inboxes, a header in the folder tree, or no folder at all.</summary>
    NoDestination,

    /// <summary>Every message is already in the destination folder.</summary>
    SameFolder,

    /// <summary>Copy the messages into the destination.</summary>
    Copy,

    /// <summary>Move the messages into the destination (they were cut).</summary>
    Move,
}

/// <summary>
/// Messages the user has copied or cut, waiting to be pasted into a folder (issue #929).
///
/// <para>Explorer's model, which is also Outlook's: cutting removes nothing. The messages stay where
/// they are until they are pasted, and only then move. A cut that is never pasted is harmless, and
/// a cut that has been pasted is cleared, so a second Ctrl+V does not try to move messages that have
/// already gone. A copy stays, so it can be pasted again into another folder.</para>
///
/// <para>Holds summaries rather than ids because the paste needs each message's account and folder
/// to group the server operations, exactly as Move to Folder does.</para>
/// </summary>
public sealed class MessageClipboard
{
    private IReadOnlyList<MailMessageSummary> _messages = Array.Empty<MailMessageSummary>();

    /// <summary>The copied or cut messages, in the order they were selected.</summary>
    public IReadOnlyList<MailMessageSummary> Messages => _messages;

    /// <summary>True when the messages were cut, so pasting moves them.</summary>
    public bool IsCut { get; private set; }

    public bool IsEmpty => _messages.Count == 0;

    /// <summary>Raised whenever the contents change, including when they are cleared.</summary>
    public event EventHandler? Changed;

    public void Copy(IEnumerable<MailMessageSummary> messages) => Set(messages, cut: false);

    public void Cut(IEnumerable<MailMessageSummary> messages) => Set(messages, cut: true);

    public void Clear()
    {
        if (IsEmpty && !IsCut) return;
        _messages = Array.Empty<MailMessageSummary>();
        IsCut = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Drops messages that have already been pasted away: a cut whose paste stopped partway keeps only
    /// the messages still where they were, so pasting again does not ask for ones that have gone.
    /// </summary>
    public void Remove(IEnumerable<MailMessageSummary> gone)
    {
        var goneKeys = gone.Select(KeyOf).ToHashSet();
        if (goneKeys.Count == 0 || IsEmpty) return;
        var kept = _messages.Where(m => !goneKeys.Contains(KeyOf(m))).ToList();
        if (kept.Count == _messages.Count) return;
        if (kept.Count == 0) { Clear(); return; }
        _messages = kept;
        Changed?.Invoke(this, EventArgs.Empty);
    }
    /// <summary>
    /// What pasting into <paramref name="destination"/> would do.
    /// <paramref name="isVirtual"/> tells real folders from the app's virtual ones (All Inboxes,
    /// saved views…), which hold no messages of their own and so cannot be pasted into.
    /// </summary>
    public MessagePasteKind Plan(MailFolderModel? destination, Func<MailFolderModel, bool> isVirtual)
    {
        if (IsEmpty) return MessagePasteKind.Nothing;

        if (destination == null ||
            destination.AccountId == Guid.Empty ||
            string.IsNullOrEmpty(destination.FullName) ||
            isVirtual(destination))
            return MessagePasteKind.NoDestination;

        if (_messages.All(m => m.AccountId == destination.AccountId &&
                               string.Equals(m.FolderName, destination.FullName, StringComparison.Ordinal)))
            return MessagePasteKind.SameFolder;

        return IsCut ? MessagePasteKind.Move : MessagePasteKind.Copy;
    }

    /// <summary>
    /// Who a message is, independent of the object holding it. The message list replaces its rows
    /// with fresh objects when it reloads in the background (#925), so the summary that was copied
    /// may no longer be the one on screen by the time it is pasted; matching by reference would
    /// then miss it.
    /// </summary>
    public static (Guid AccountId, string FolderName, string MessageId) KeyOf(MailMessageSummary m) =>
        (m.AccountId, m.FolderName, m.MessageId);

    private void Set(IEnumerable<MailMessageSummary> messages, bool cut)
    {
        var list = messages.DistinctBy(KeyOf).ToList();
        if (list.Count == 0) return;
        _messages = list;
        IsCut = cut;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

