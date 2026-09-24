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
    [GeneratedRegex(@"cid:([^""'\s)>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CidReference();

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

    /// <summary>Every Content-ID the text refers to, in the case written.</summary>
    public static HashSet<string> ReferencedContentIds(params string?[] texts)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in texts)
            if (!string.IsNullOrEmpty(text))
                foreach (Match m in CidReference().Matches(text))
                    ids.Add(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value));
        return ids;
    }

    /// <summary>The picture parts of a message that have a Content-ID, with their bytes.</summary>
    public static List<AttachmentModel> FromMime(MimeMessage message)
    {
        var result = new List<AttachmentModel>();
        foreach (var part in message.BodyParts.OfType<MimePart>())
        {
            if (string.IsNullOrEmpty(part.ContentId) || part.Content is null) continue;
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
    public static async Task<List<AttachmentModel>> FetchAsync(
        IMailService mail, Guid accountId, string folderName, string messageId,
        IReadOnlySet<string> wanted, CancellationToken ct = default)
    {
        if (wanted.Count == 0) return [];
        try
        {
            using var raw = new MemoryStream();
            await mail.CopyOriginalMessageToAsync(accountId, folderName, messageId, raw, ct);
            raw.Position = 0;
            var message = await MimeMessage.LoadAsync(raw, ct);
            return FromMime(message).Where(i => wanted.Contains(i.ContentId!)).ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogService.Log($"InlineImages: could not fetch pictures of message {messageId}: {ex.Message}");
            return [];
        }
    }
}
