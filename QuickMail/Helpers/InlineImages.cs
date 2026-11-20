using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Helpers;

/// <summary>
/// Pictures shown inside a message body (#729): the HTML refers to each as <c>cid:</c> plus
/// its Content-ID, and the bytes travel as a <c>multipart/related</c> part. Helpers for making
/// Content-IDs, finding which ones a body still uses, and reading the pictures out of a stored
/// message — the one route that works the same for IMAP, Graph and POP accounts.
/// </summary>
public static partial class InlineImages
{
    // Picture references are read only where a picture really is: the src of an <img> tag in
    // HTML, or an image in Markdown. Never "cid:" found anywhere in the text — a sender could
    // type "cid:x", "src=cid:x" or "](cid:x)" as words, and have a part the reader never saw
    // fetched with a reply and sent on with it (#729 security review). Text in HTML is encoded,
    // so typed words can never form an <img> tag.

    [GeneratedRegex(@"(?:^|[\s/])src\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex SrcAttribute();

    [GeneratedRegex(@"!\[(?:\\\]|[^\]])*\]\(cid:(?<v>[^)\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownPicture();

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ImgTag();

    [GeneratedRegex(@"\s(?:alt=""""|title=""no description"")", RegexOptions.IgnoreCase)]
    private static partial Regex UndescribedMarkers();

    /// <summary>
    /// Markdown can only write an undescribed picture as <c>![](… "no description")</c>, which
    /// Markdig renders as <c>alt="" title="no description"</c> — decorative, with a tooltip. Put
    /// it back the way HTML mode sends it: no alt attribute at all, and no title.
    /// </summary>
    public static string RestoreUndescribed(string html) =>
        ImgTag().Replace(html, m => m.Value.Contains("title=\"no description\"", StringComparison.OrdinalIgnoreCase)
            ? UndescribedMarkers().Replace(m.Value, string.Empty)
            : m.Value);

    /// <summary>A new, globally unique Content-ID for a picture added in compose.</summary>
    public static string NewContentId() => $"{Guid.NewGuid():N}@quickmail";

    /// <summary>True when the text (HTML or Markdown) refers to any <c>cid:</c> picture.</summary>
    public static bool HasReferences(string? text) =>
        !string.IsNullOrEmpty(text) && text.Contains("cid:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every Content-ID an <c>&lt;img&gt;</c> in the HTML shows, in the case written.</summary>
    public static HashSet<string> ReferencedContentIds(params string?[] htmls)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var html in htmls)
        {
            if (string.IsNullOrEmpty(html)) continue;
            foreach (Match tag in ImgTag().Matches(html))
            {
                var src = SrcAttribute().Match(tag.Value[4..]);
                if (!src.Success) continue;
                var value = System.Net.WebUtility.HtmlDecode(src.Groups["v"].Value).Trim();
                // "cid:<a@x>" names the same part as "cid:a@x", as the reading pane reads it.
                if (value.StartsWith("cid:", StringComparison.OrdinalIgnoreCase)
                    && value[4..].Trim('<', '>') is { Length: > 0 } id)
                    ids.Add(id);
            }
        }
        return ids;
    }

    /// <summary>Every Content-ID a Markdown image — <c>![alt](cid:…)</c> — shows. For the user's own Markdown source only.</summary>
    public static HashSet<string> ReferencedInMarkdown(string? markdown)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(markdown))
            foreach (Match m in MarkdownPicture().Matches(markdown))
                ids.Add(m.Groups["v"].Value);
        return ids;
    }

    /// <summary>
    /// The picture parts of a message that have a Content-ID, with their bytes — only those in
    /// <paramref name="wanted"/> when it is given, so unwanted parts are never decoded.
    /// </summary>
    public static List<AttachmentModel> FromMime(MimeMessage message, IReadOnlySet<string>? wanted = null)
    {
        var result = new List<AttachmentModel>();
        foreach (var part in message.BodyParts.OfType<MimePart>())
        {
            if (string.IsNullOrEmpty(part.ContentId) || part.Content is null) continue;
            if (wanted is not null && !wanted.Contains(part.ContentId)) continue;
            if (!part.ContentType.MediaType.Equals("image", StringComparison.OrdinalIgnoreCase)) continue;
            using var buffer = new MemoryStream();
            part.Content.DecodeTo(buffer);
            result.Add(new AttachmentModel
            {
                FileName    = part.FileName ?? "image",
                ContentType = part.ContentType.MimeType,
                FileSize    = buffer.Length,
                Content     = buffer.ToArray(),
                ContentId   = part.ContentId,
            });
        }
        return result;
    }

    /// <summary>
    /// Downloads a stored message and returns the pictures among <paramref name="wanted"/> that
    /// it has. Used when reopening a draft and when a reply or forward quotes a message with
    /// pictures. Returns what it could get; a failure is logged and gives an empty list, so a
    /// message still opens when its pictures cannot be fetched.
    /// </summary>
    /// <param name="maxBytes">
    /// The most of the original to download. A message larger than this is abandoned part way and
    /// its pictures left out — getting at a picture must never mean pulling down a huge message.
    /// </param>
    public static async Task<List<AttachmentModel>> FetchAsync(
        IMailService mail, Guid accountId, string folderName, string messageId,
        IReadOnlySet<string> wanted, long maxBytes = 50L * 1024 * 1024, CancellationToken ct = default)
    {
        if (wanted.Count == 0) return [];
        try
        {
            using var raw = new CappedMemoryStream(maxBytes);
            await mail.CopyOriginalMessageToAsync(accountId, folderName, messageId, raw, ct);
            raw.Position = 0;
            var message = await MimeMessage.LoadAsync(raw, ct);
            return FromMime(message, wanted);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogService.Log($"InlineImages: could not fetch pictures of message {messageId}: {ex.Message}");
            return [];
        }
    }

    /// <summary>A memory stream that refuses to grow past a limit, ending a download that would.</summary>
    private sealed class CappedMemoryStream(long maxBytes) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            Check(1);
            base.WriteByte(value);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Check(count);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Check(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }

        private void Check(int adding)
        {
            if (Length + adding > maxBytes)
                throw new InvalidDataException($"The message is larger than {maxBytes / (1024 * 1024)} MB; its pictures are not downloaded.");
        }
    }
}
