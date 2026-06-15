// Deviation from spec: MailMessageSummary does not carry Cc, ReplyTo, MessageId, or Size.
// Those fields are on MailMessageDetail only. When detail is null the rows show "(not loaded)".
// RawHeaders does not exist on MailMessageDetail so the raw-headers expander is never shown.

using System.Collections.Generic;
using QuickMail.Models;

namespace QuickMail.Helpers;

public static class MessagePropertiesBuilder
{
    public static (string Title, IReadOnlyList<PropertySection> Sections)
        Build(MailMessageSummary summary, MailMessageDetail? detail, string accountName)
    {
        var headers = new List<PropertyItem>
        {
            new("From",       NoneIfBlank(summary.From)),
            new("To",         NoneIfBlank(summary.To)),
            new("Cc",         detail is not null ? NoneIfBlank(detail.Cc) : "(not loaded)"),
            new("Reply-To",   detail is not null ? NoneIfBlank(detail.ReplyTo) : "(not loaded)"),
            new("Subject",    NoneIfBlank(summary.Subject)),
            new("Date",       summary.Date.ToLocalTime().ToString("f")),
            new("Message-ID", detail is not null ? NoneIfBlank(detail.MessageId) : "(not loaded)"),
        };

        var storage = new List<PropertyItem>
        {
            new("Account",  accountName),
            new("Folder",   summary.FolderName),
            new("IMAP UID", summary.MessageId),
            new("Flag",     summary.IsFlagged ? summary.FlagLabel : "None"),
            new("Status",   FormatStatus(summary)),
        };

        var sections = new List<PropertySection>
        {
            new("Headers", headers),
            new("Storage", storage),
        };

        if (detail is not null)
        {
            var content = BuildContentSection(detail);
            if (content.Items.Count > 0)
                sections.Add(content);
        }

        return ("Message Properties", sections);
    }

    private static PropertySection BuildContentSection(MailMessageDetail d)
    {
        var items = new List<PropertyItem>();

        items.Add(new("Format",
            d.HtmlBody.Length > 0 && d.PlainTextBody.Length > 0
                ? "HTML with plain-text alternative"
                : d.HtmlBody.Length > 0
                    ? "HTML only"
                    : "Plain text"));

        if (d.Attachments.Count > 0)
            items.Add(new("Attachments",
                $"{d.Attachments.Count} attachment{(d.Attachments.Count == 1 ? "" : "s")}"));

        return new("Content", items);
    }

    private static string NoneIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "(none)" : s;

    private static string FormatStatus(MailMessageSummary s)
    {
        var parts = new List<string>();
        if (!s.IsRead)      parts.Add("Unread");
        if (s.IsReplied)    parts.Add("Replied");
        if (s.IsForwarded)  parts.Add("Forwarded");
        return parts.Count > 0 ? string.Join(", ", parts) : "Read";
    }
}
