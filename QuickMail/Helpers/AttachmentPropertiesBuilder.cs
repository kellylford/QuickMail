// Deviation from spec: AttachmentModel uses FileSize (not Size) and does not carry
// Encoding, ContentId, or IsInline fields — those rows are omitted.

using System.Collections.Generic;
using QuickMail.Models;

namespace QuickMail.Helpers;

public static class AttachmentPropertiesBuilder
{
    public static (string Title, IReadOnlyList<PropertySection> Sections)
        Build(AttachmentModel attachment)
    {
        var items = new List<PropertyItem>
        {
            new("File name",  NoneIfBlank(attachment.FileName)),
            new("MIME type",  attachment.ContentType),
            new("Size",       FormatBytes(attachment.FileSize)),
        };
        return ("Attachment Properties", [new("File", items)]);
    }

    private static string NoneIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "(unnamed)" : s;

    private static string FormatBytes(long bytes) => bytes switch
    {
        <= 0          => "Unknown",
        < 1024        => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _             => $"{bytes / (1024.0 * 1024):F1} MB",
    };
}
