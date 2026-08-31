using System;

namespace QuickMail.Models;

/// <summary>
/// The three parts that identify one stored message row: account, folder, id (#637).
/// <para>Matching on fewer than all three has been the single most recurring source of defects in
/// offline drafts — the same local id can exist under two accounts, and the same account can hold
/// the same id in two folders. Naming the key as a type makes it awkward to pass two of the three.</para>
/// </summary>
public readonly record struct DraftRowKey(Guid AccountId, string FolderName, string MessageId)
{
    /// <summary>True when this key names the given summary's row.</summary>
    public bool Matches(MailMessageSummary summary) =>
        summary is not null &&
        summary.MessageId  == MessageId &&
        summary.AccountId  == AccountId &&
        summary.FolderName == FolderName;
}
