using System;
using System.Collections.Generic;
using QuickMail.Models;

namespace QuickMail.ViewModels;

// The one-time attachment-flag pass (#931): when it finds that cached messages have attachments, the rows already on
// screen say so at once, instead of when their folder is next loaded.
public partial class MainViewModel
{
    private void OnAttachmentFlagsFound(Guid accountId, string folderName, IReadOnlyCollection<string> messageIds)
    {
        // A list narrowed by attachments (the With Attachments filter, or a search) gains the rows just found.
        if (ApplyAttachmentFlags(_rawMessages, accountId, folderName, messageIds) > 0
            && (ActiveFilter == MessageFilter.WithAttachments || !string.IsNullOrWhiteSpace(SearchText)))
            ApplyFiltersAndSearch();
    }

    /// <summary>Sets the flag on the matching rows; returns how many changed.</summary>
    internal static int ApplyAttachmentFlags(IEnumerable<MailMessageSummary> rows, Guid accountId, string folderName,
        IReadOnlyCollection<string> messageIds)
    {
        if (messageIds.Count == 0) return 0;
        var ids = new HashSet<string>(messageIds, StringComparer.Ordinal);
        var changed = 0;
        foreach (var message in rows)
        {
            if (message.AccountId == accountId
                && string.Equals(message.FolderName, folderName, StringComparison.Ordinal)
                && ids.Contains(message.MessageId)
                && !message.HasAttachments)
            {
                message.HasAttachments = true;
                changed++;
            }
        }
        return changed;
    }
}
