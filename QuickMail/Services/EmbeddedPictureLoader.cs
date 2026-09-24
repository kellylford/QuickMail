using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Fetches the pictures sent inside a message (its <c>cid:</c> parts) for the reading pane,
/// message tabs and message windows. They contact no server beyond the account's own, so they
/// carry no tracking cost and are shown by default (<see cref="ConfigModel.ShowEmbeddedPictures"/>).
///
/// IMAP and POP details list the picture parts, so only those parts are downloaded. Graph
/// details and ones read from the local cache do not, so the stored original is read instead.
/// Recently shown messages' pictures are kept in memory, so going back to a message or
/// re-rendering it on a theme change does not fetch them again.
/// </summary>
public static class EmbeddedPictureLoader
{
    /// <summary>Picture types the reading pane is given. Never SVG: an SVG document can carry script.</summary>
    private static readonly HashSet<string> DisplayableTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/jpg", "image/pjpeg", "image/gif", "image/webp", "image/bmp",
    };

    /// <summary>True for a picture type safe to hand to the reading pane.</summary>
    public static bool IsDisplayable(string? contentType) =>
        contentType is not null && DisplayableTypes.Contains(contentType.Split(';')[0].Trim());

    private const long CacheBytes = 64L * 1024 * 1024;

    /// <summary>Largest attachment total for which the whole original is downloaded to get at its pictures.</summary>
    private const long MaxOriginalBytesForPictures = 5L * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly LinkedList<(string Key, IReadOnlyDictionary<string, AttachmentModel> Pictures, long Bytes)> Cache = new();
    private static readonly Dictionary<string, Task<IReadOnlyDictionary<string, AttachmentModel>>> InFlight = new();
    private static long _cachedBytes;

    private static readonly IReadOnlyDictionary<string, AttachmentModel> None =
        new Dictionary<string, AttachmentModel>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The displayable pictures the message's HTML shows, keyed by Content-ID (case-insensitive).
    /// Never throws for a failed fetch: a picture that cannot be had is simply absent, and the
    /// reading pane shows its alt text in its place.
    /// </summary>
    public static Task<IReadOnlyDictionary<string, AttachmentModel>> LoadAsync(
        IMailService mail, MailMessageDetail detail)
    {
        var key = $"{detail.AccountId:N}|{detail.FolderName}|{detail.MessageId}";
        lock (Gate)
        {
            for (var node = Cache.First; node != null; node = node.Next)
            {
                if (node.Value.Key != key) continue;
                Cache.Remove(node);
                Cache.AddFirst(node); // most recently used
                return Task.FromResult(node.Value.Pictures);
            }
            if (InFlight.TryGetValue(key, out var running)) return running;
            // Not cancelled by the caller: the fetch is shared with anything else showing the same
            // message, and a caller that moves on simply stops waiting for it.
            // On the thread pool: reading a stored original parses the whole message, which must not
            // happen on the UI thread that raised the picture request.
            var task = Task.Run(() => FetchAsync(mail, detail, key, CancellationToken.None));
            // A fetch that finished without waiting (nothing to fetch) has already run its
            // finally; recording it now would leave a stale entry nothing ever removes.
            if (!task.IsCompleted) InFlight[key] = task;
            return task;
        }
    }

    private static async Task<IReadOnlyDictionary<string, AttachmentModel>> FetchAsync(
        IMailService mail, MailMessageDetail detail, string key, CancellationToken ct)
    {
        try
        {
            var wanted = InlineImages.ReferencedContentIds(detail.HtmlBody);
            if (wanted.Count == 0) return None;

            List<AttachmentModel> pictures;
            bool complete = true;
            var listed = detail.InlineImages
                .Where(p => p.ContentId is not null && wanted.Contains(p.ContentId) && IsDisplayable(p.ContentType))
                .ToList();
            if (listed.Count > 0)
            {
                pictures = [];
                foreach (var part in listed)
                {
                    if (part.PartSpecifier is null) continue;
                    try
                    {
                        var bytes = await mail.DownloadAttachmentAsync(
                            detail.AccountId, detail.FolderName, detail.MessageId, part.PartSpecifier, ct);
                        pictures.Add(new AttachmentModel
                        {
                            FileName = part.FileName, ContentType = part.ContentType,
                            FileSize = bytes.LongLength, Content = bytes, ContentId = part.ContentId,
                        });
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        complete = false;
                        LogService.Log($"EmbeddedPictureLoader: part {part.PartSpecifier} of {detail.MessageId}: {ex.Message}");
                    }
                }
            }
            else
            {
                // No part list (Graph, or a detail from the local cache): the pictures come out of
                // the stored original, which means downloading all of it. Not for a message carrying
                // large attachments — a signature logo is not worth 25 MB; its alt text stands in.
                if (detail.Attachments.Sum(a => a.FileSize) > MaxOriginalBytesForPictures)
                    return None;
                pictures = (await InlineImages.FetchAsync(mail, detail.AccountId, detail.FolderName, detail.MessageId, wanted, ct))
                    .Where(p => IsDisplayable(p.ContentType))
                    .ToList();
            }

            var result = pictures
                .GroupBy(p => p.ContentId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            // A picture that failed is tried again next time the message is shown, not remembered as missing.
            if (complete) Remember(key, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            return None;
        }
        catch (Exception ex)
        {
            LogService.Log($"EmbeddedPictureLoader: {detail.MessageId}: {ex.Message}");
            return None;
        }
        finally
        {
            lock (Gate) InFlight.Remove(key);
        }
    }

    private static void Remember(string key, IReadOnlyDictionary<string, AttachmentModel> pictures)
    {
        var bytes = pictures.Values.Sum(p => p.Content?.LongLength ?? 0);
        if (bytes == 0 || bytes > CacheBytes) return;
        lock (Gate)
        {
            Cache.AddFirst((key, pictures, bytes));
            _cachedBytes += bytes;
            while (_cachedBytes > CacheBytes && Cache.Last is { } oldest)
            {
                _cachedBytes -= oldest.Value.Bytes;
                Cache.RemoveLast();
            }
        }
    }

    /// <summary>Empties the cache — for tests.</summary>
    internal static void ClearCache()
    {
        lock (Gate)
        {
            Cache.Clear();
            _cachedBytes = 0;
        }
    }
}
