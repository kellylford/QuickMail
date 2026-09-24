using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Helpers;

/// <summary>
/// What a saved copy says about where the message lives — facts the message itself does not carry,
/// so they come from the app rather than from the headers (#728).
/// </summary>
/// <param name="AccountLabel">The account, as a person would name it: "Kelly Ford (kelly@example.com)".</param>
/// <param name="FolderPath">The folder, as a path of display names: "Inbox/Receipts". Never a Graph id.</param>
/// <param name="FlagName">The flag's name when the message is flagged, else null.</param>
/// <param name="SavedAt">When the copy was made.</param>
public sealed record MessageSaveContext(string AccountLabel, string FolderPath, string? FlagName, DateTimeOffset SavedAt);

/// <summary>A picture a saved message carries inside its own file: its bytes, and the picture type they are.</summary>
public sealed record SavedPicture(byte[] Bytes, string ContentType);

/// <summary>
/// The pictures a saved web page, PDF or printout may show: the message's own, by Content-ID, and
/// pictures from the web that QuickMail already holds, by address. Saving never fetches anything
/// from the web; <see cref="Web"/> answers only from what was already loaded.
/// </summary>
public sealed record SavedPictures(IReadOnlyDictionary<string, SavedPicture> Embedded, Func<string, SavedPicture?> Web);

/// <summary>
/// Pure builders for a saved message: its file name, and its text and web-page forms (#728). No I/O,
/// no UI — the orchestration lives in <c>MessageSaver</c>, so everything here is unit-testable.
/// </summary>
public static class MessageExport
{
    /// <summary>Longest subject kept in a file name, before the sender and date are added.</summary>
    internal const int MaxSubjectChars = 80;
    /// <summary>Longest sender name kept in a file name.</summary>
    internal const int MaxSenderChars = 40;

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM\u00B9", "COM\u00B2", "COM\u00B3",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
    };

    /// <summary>
    /// True when Windows would open a device rather than a file for this name. Windows judges the part
    /// before the first dot, trimmed — so "NUL.x - Jane.eml" is the NUL device, and a save to it would
    /// report success and write nothing.
    /// </summary>
    internal static bool IsReservedDeviceName(string fileName)
    {
        var dot = fileName.IndexOf('.');
        var head = (dot >= 0 ? fileName[..dot] : fileName).Trim(' ', '.');
        return ReservedDeviceNames.Contains(head);
    }

    // ── File names ────────────────────────────────────────────────────────────

    /// <summary>
    /// The default file name: subject first, then sender, then the date — "Your order has shipped -
    /// Jane Smith - 2026-09-17 1432.eml". Subject first because that is what someone scanning a folder
    /// of saved mail looks for; the date keeps two messages with one subject apart.
    /// </summary>
    public static string BuildFileName(MailMessageSummary message, MessageSaveFormat format)
    {
        var subject = Clip(CleanForFileName(message.Subject), MaxSubjectChars);
        if (subject.Length == 0) subject = "No subject";

        var sender = Clip(CleanForFileName(SenderName(message.From)), MaxSenderChars);
        var date   = message.Date == default
            ? string.Empty
            : message.Date.ToLocalTime().ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture);

        var parts = new List<string> { subject };
        if (sender.Length > 0) parts.Add(sender);
        if (date.Length > 0)   parts.Add(date);

        var stem = string.Join(" - ", parts);
        var name = stem + MessageSaveFormats.Extension(format);
        return IsReservedDeviceName(name) ? "_" + name : name;
    }

    /// <summary>
    /// The display name of the first sender in a From header, else its address, else empty.
    /// </summary>
    public static string SenderName(string? from)
    {
        if (string.IsNullOrWhiteSpace(from)) return string.Empty;
        if (InternetAddressList.TryParse(from, out var list))
        {
            var mailbox = list.Mailboxes.FirstOrDefault();
            if (mailbox != null)
                return !string.IsNullOrWhiteSpace(mailbox.Name) ? mailbox.Name.Trim() : mailbox.Address ?? string.Empty;
        }
        return from.Trim();
    }

    /// <summary>
    /// Makes text from a message header safe as part of a Windows file name. Unlike
    /// <see cref="AttachmentSafety.SanitizeFileName"/>, a separator is NOT a directory boundary here:
    /// "Q3 / Q4 plan" is a subject, not a path, and must not be cut down to " Q4 plan". Every
    /// character Windows rejects becomes a space; invisible formatting characters are dropped (a
    /// right-to-left override would otherwise let a subject disguise the extension that follows it);
    /// runs of whitespace collapse; trailing dots and spaces go, as Windows would drop them silently.
    /// </summary>
    public static string CleanForFileName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            var category = char.GetUnicodeCategory(c);
            if (category is UnicodeCategory.Format or UnicodeCategory.Control
                         or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                if (category != UnicodeCategory.Format) sb.Append(' ');
                continue;
            }
            // Surrogates pass through: a lone one never reaches here from a .NET string built by the
            // parser, and a pair is a legitimate character (an emoji in a subject).
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? ' ' : c);
        }
        var collapsed = string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Trim().TrimEnd('.', ' ').TrimStart('.', ' ');
    }

    private static string Clip(string text, int max)
    {
        if (text.Length <= max) return text;
        var cut = text[..max];
        // Never split a surrogate pair.
        if (char.IsHighSurrogate(cut[^1])) cut = cut[..^1];
        return cut.TrimEnd('.', ' ');
    }

    /// <summary>
    /// <paramref name="fileName"/> in <paramref name="folder"/>, or the first "name (2).ext",
    /// "name (3).ext"… that does not exist yet. Saving never overwrites without being asked; the
    /// Save As dialog asks, and everywhere else a second copy is kept beside the first.
    /// </summary>
    public static string UniquePath(string folder, string fileName, Func<string, bool> exists)
    {
        var path = Path.Combine(folder, fileName);
        if (!exists(path)) return path;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext  = Path.GetExtension(fileName);
        for (var n = 2; n < 10_000; n++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({n}){ext}");
            if (!exists(candidate)) return candidate;
        }
        // Ten thousand copies of one name: give up on counting and make it unique outright.
        return Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){ext}");
    }

    // ── The details block ─────────────────────────────────────────────────────

    /// <summary>
    /// The human-readable details every saved copy leads with, in reading order. Only fields with a
    /// value appear — no "Cc: (none)". Labels are words a person would use, not header names.
    /// </summary>
    public static IReadOnlyList<(string Label, string Value)> BuildDetails(MailMessageDetail detail, MessageSaveContext context)
    {
        var rows = new List<(string, string)>();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) rows.Add((label, value.Trim()));
        }

        Add("Subject", string.IsNullOrWhiteSpace(detail.Subject) ? "(no subject)" : detail.Subject);
        Add("From", detail.From);
        if (!string.Equals(detail.ReplyTo?.Trim(), detail.From?.Trim(), StringComparison.OrdinalIgnoreCase))
            Add("Reply to", detail.ReplyTo);
        Add("To", detail.To);
        Add("Cc", detail.Cc);
        if (detail.Date != default) Add("Date", FormatDate(detail.Date));

        if (detail.CalendarInvite is { } invite)
        {
            Add("Invitation", invite.Summary);
            Add("When", FormatInviteTime(invite));
            Add("Where", invite.Location);
            Add("Organizer", !string.IsNullOrWhiteSpace(invite.OrganizerName) ? invite.OrganizerName : invite.Organizer);
        }

        Add("Account", context.AccountLabel);
        Add("Folder", context.FolderPath);
        Add("Status", StatusText(detail, context.FlagName));

        if (detail.Attachments.Count > 0)
            Add(detail.Attachments.Count == 1 ? "Attachment" : $"Attachments ({detail.Attachments.Count})",
                string.Join(", ", detail.Attachments.Select(a =>
                    $"{(string.IsNullOrWhiteSpace(a.FileName) ? "unnamed attachment" : a.FileName)} ({a.FileSizeDisplay})")));

        Add("Saved", $"{FormatDate(context.SavedAt)}, from QuickMail");
        return rows;
    }

    /// <summary>"Read, flagged (Follow up), replied, forwarded" — the state a person would describe.</summary>
    internal static string StatusText(MailMessageSummary message, string? flagName)
    {
        var parts = new List<string> { message.IsRead ? "Read" : "Unread" };
        if (message.IsFlagged)
            parts.Add(string.IsNullOrWhiteSpace(flagName) ? "flagged" : $"flagged ({flagName})");
        if (message.IsReplied)   parts.Add("replied");
        if (message.IsForwarded) parts.Add("forwarded");
        return string.Join(", ", parts);
    }

    /// <summary>"Thursday, September 17, 2026 2:32 PM", in local time and the user's own date style.</summary>
    internal static string FormatDate(DateTimeOffset date)
    {
        var local = date.ToLocalTime();
        return $"{local.ToString("D", CultureInfo.CurrentCulture)} {local.ToString("t", CultureInfo.CurrentCulture)}";
    }

    private static string? FormatInviteTime(IcsModel invite)
    {
        if (invite.StartTime is not { } start) return null;
        if (invite.IsAllDay) return start.ToString("D", CultureInfo.CurrentCulture) + ", all day";
        var text = $"{start.ToString("D", CultureInfo.CurrentCulture)} {start.ToString("t", CultureInfo.CurrentCulture)}";
        if (invite.EndTime is { } end)
            text += end.Date == start.Date
                ? $" to {end.ToString("t", CultureInfo.CurrentCulture)}"
                : $" to {end.ToString("D", CultureInfo.CurrentCulture)} {end.ToString("t", CultureInfo.CurrentCulture)}";
        return text;
    }

    // ── Text ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The text form: the details, a rule, then the body — the sender's own plain-text part when
    /// there is one, else text extracted from the HTML, with a line saying so. Never truncated: the
    /// reading pane's length clamp is a rendering limit, and a saved copy is not rendered.
    /// </summary>
    public static string BuildTextDocument(MailMessageDetail detail, MessageSaveContext context)
    {
        var sb = new StringBuilder();
        foreach (var (label, value) in BuildDetails(detail, context))
            sb.Append(label).Append(": ").Append(OneLine(value)).Append("\r\n");

        sb.Append("\r\n").Append(new string('-', 40)).Append("\r\n\r\n");

        var (body, derived) = PlainBody(detail);
        if (derived)
            sb.Append("[This message has no plain-text version. This is text extracted from its formatted version.]\r\n\r\n");
        sb.Append(NormalizeNewlines(body));
        if (!body.EndsWith('\n')) sb.Append("\r\n");
        return sb.ToString();
    }

    private static (string Text, bool Derived) PlainBody(MailMessageDetail detail)
    {
        if (!string.IsNullOrWhiteSpace(detail.PlainTextBody)) return (detail.PlainTextBody, false);
        if (!string.IsNullOrWhiteSpace(detail.HtmlBody))
            return (MessageBodyHtmlBuilder.HtmlToText(detail.HtmlBody), true);
        return (string.Empty, false);
    }

    /// <summary>
    /// A header value as one line of plain text. Anything that breaks a line in some viewer — CR, LF,
    /// NEL, the Unicode line and paragraph separators, vertical tab, form feed — would let a header
    /// start what looks like a detail line of its own ("Date: …" forged inside a subject), and the
    /// other control characters (ESC among them) have no business in a text file. All become spaces.
    /// Bidirectional overrides and isolates are dropped: a right-to-left override in a display name
    /// reorders the address that follows it on screen.
    /// </summary>
    internal static string OneLine(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (IsBidiControl(c)) continue;
            var category = char.GetUnicodeCategory(c);
            sb.Append(category is UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                ? ' ' : c);
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsBidiControl(char c) =>
        c is '\u061C' or '\u200E' or '\u200F' or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069');

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");

    // ── Web page ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The web-page form: one self-contained file, the details as a table, then the body as the
    /// reading pane renders it. The sender's HTML goes through the reading pane's own sanitizer, and
    /// the page carries the reading pane's strict Content-Security-Policy, so opening it in a browser
    /// runs nothing and fetches nothing. Pictures in <paramref name="pictures"/> are written into the
    /// file itself, as data, so the page stays one self-contained file that contacts nothing; any
    /// other picture is left out and its alt text stands in for it.
    /// <para>Sender HTML that cannot be sanitized in time falls back to the plain-text body, with a
    /// note, rather than writing a partially stripped document (the reading pane's own rule).</para>
    /// </summary>
    public static string BuildHtmlDocument(MailMessageDetail detail, MessageSaveContext context,
        SavedPictures? pictures = null)
    {
        var e = (Func<string?, string>)(s => WebUtility.HtmlEncode(s ?? string.Empty));
        var title = string.IsNullOrWhiteSpace(detail.Subject) ? "(no subject)" : detail.Subject.Trim();

        var wrotePicture = false;
        string? DataUri(SavedPicture? picture)
        {
            if (picture is null || !EmbeddedPictureLoader.IsDisplayable(picture.ContentType)) return null;
            wrotePicture = true;
            return $"data:{picture.ContentType.Split(';')[0].Trim().ToLowerInvariant()};base64,{Convert.ToBase64String(picture.Bytes)}";
        }
        string? fragment = null;
        var sanitized = !string.IsNullOrWhiteSpace(detail.HtmlBody)
            && MessageBodyHtmlBuilder.TryBuildSanitizedBodyFragment(detail.HtmlBody,
                pictures is { Embedded.Count: > 0 } ? cid => DataUri(pictures.Embedded.GetValueOrDefault(cid)) : null,
                pictures is null ? null : url => DataUri(pictures.Web(url)),
                out fragment);
        // Pictures written into the page are allowed as data and nothing else: still nothing is fetched.
        var csp = wrotePicture
            ? MessageBodyHtmlBuilder.StrictCspContent.Replace("img-src 'none';", "img-src data:;", StringComparison.Ordinal)
            : MessageBodyHtmlBuilder.StrictCspContent;

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"")
          .Append(csp).Append("\">");
        sb.Append("<meta name=\"referrer\" content=\"no-referrer\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<title>").Append(e(title)).Append("</title>");
        sb.Append("<style>")
          .Append("body{font-family:'Segoe UI',Arial,sans-serif;font-size:15px;line-height:1.5;")
          .Append("max-width:60em;margin:0 auto;padding:16px;color:#1a1a1a;background:#fff;word-break:break-word;}")
          .Append("h1{font-size:1.4em;margin:0 0 12px 0;}")
          .Append("table.qm-details{border-collapse:collapse;margin:0 0 16px 0;}")
          .Append("table.qm-details th{text-align:left;vertical-align:top;padding:2px 16px 2px 0;white-space:nowrap;}")
          .Append("table.qm-details td{vertical-align:top;padding:2px 0;}")
          .Append("hr{border:0;border-top:1px solid #888;margin:16px 0;}")
          .Append(".qm-note{border-left:3px solid #888;padding-left:8px;color:#444;}")
          .Append(".qm-plain{white-space:pre-wrap;}")
          // Nothing in the message body can paint outside its own box: contain:paint clips even
          // absolutely and fixed-positioned content, so the details above cannot be covered.
          .Append("main{contain:paint;position:relative;}")
          .Append("main table{max-width:100%;border-collapse:collapse;}main td,main th{vertical-align:top;}")
          .Append("main img{max-width:100%;height:auto;}")
          .Append("a{color:#0645ad;}")
          .Append("@media print{body{max-width:none;padding:0;}}")
          .Append("</style></head><body>");

        sb.Append("<header><h1><bdi>").Append(e(OneLine(title))).Append("</bdi></h1>");
        sb.Append("<table class=\"qm-details\"><caption style=\"position:absolute;left:-10000px\">Message details</caption><tbody>");
        foreach (var (label, value) in BuildDetails(detail, context))
        {
            if (label == "Subject") continue;   // it is the heading
            // <bdi>: a value's own text direction cannot spill into the row around it.
            sb.Append("<tr><th scope=\"row\">").Append(e(label)).Append("</th><td><bdi>")
              .Append(e(OneLine(value))).Append("</bdi></td></tr>");
        }
        sb.Append("</tbody></table></header><hr><main>");

        if (sanitized)
        {
            sb.Append(fragment);
        }
        else
        {
            var (text, derived) = PlainBody(detail);
            var fellBack = !string.IsNullOrWhiteSpace(detail.HtmlBody);
            if (fellBack)
                sb.Append("<p class=\"qm-note\">This message's formatting could not be kept, so this is its text.</p>");
            else if (derived)
                sb.Append("<p class=\"qm-note\">This message has no plain-text version. This is text extracted from its formatted version.</p>");
            sb.Append("<div class=\"qm-plain\">")
              .Append(MessageBodyHtmlBuilder.AutoLinkPlainTextUrls(e(text)))
              .Append("</div>");
        }

        sb.Append("</main></body></html>");
        return sb.ToString();
    }
}
