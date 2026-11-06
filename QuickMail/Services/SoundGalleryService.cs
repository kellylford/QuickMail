using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>A scheme listed in the gallery. A screen reader reads the row from <see cref="ToString"/>.</summary>
/// <param name="Id">Its folder in the gallery repo, and its file's name without the extension.</param>
/// <param name="Name">What it's called.</param>
/// <param name="Author">Who made it.</param>
/// <param name="Description">A sentence or two about it.</param>
/// <param name="License">The licence it's shared under, such as CC-BY-4.0.</param>
/// <param name="File">Its .qmsounds file in the gallery, always <c>&lt;Id&gt;-&lt;first 16 of Sha256&gt;.qmsounds</c>, so a
/// changed scheme is a new file and no cache can pair it with the wrong listing.</param>
/// <param name="Size">The file's exact size in bytes.</param>
/// <param name="Sha256">The file's SHA-256, as lowercase hex.</param>
/// <param name="EventsWithSound">How many events it gives a sound.</param>
public sealed record SoundGalleryEntry(
    string Id, string Name, string Author, string Description, string License,
    string File, long Size, string Sha256, int EventsWithSound)
{
    /// <summary>What the gallery's list says for it.</summary>
    public string AccessibleName =>
        $"{Name}, by {Author}. {EventsWithSound} of {SoundCatalog.Events.Count} events have a sound";

    /// <summary>The row's help text: its description and licence.</summary>
    public string HelpText => (Description.Length > 0 ? Description + " " : string.Empty) + $"Licence: {License}.";

    public override string ToString() => AccessibleName;
}

/// <summary>The gallery couldn't be read, or a scheme from it couldn't be fetched; the message is for the person.</summary>
public sealed class SoundGalleryException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Lists and fetches the sound schemes in the gallery (#937).</summary>
public interface ISoundGalleryService
{
    /// <summary>The gallery's schemes, from a signed index. Throws <see cref="SoundGalleryException"/>.</summary>
    Task<IReadOnlyList<SoundGalleryEntry>> LoadAsync(CancellationToken ct);

    /// <summary>
    /// Downloads a scheme listed by <see cref="LoadAsync"/> into a temp file, checked against the
    /// index's size and hash, and returns its path. The caller deletes it. Throws <see cref="SoundGalleryException"/>.
    /// </summary>
    Task<string> DownloadAsync(SoundGalleryEntry entry, CancellationToken ct);
}

/// <summary>
/// Reads the sound scheme gallery: <c>index.signed</c>, a base64 signature line followed by the exact
/// bytes of the index it signs (one file, so a cache can never pair an index with another's signature),
/// and one <c>.qmsounds</c> file per scheme, all in one place: GitHub Pages, or a folder or URL given with
/// <c>--soundGallery</c> (#937). Everything from there is treated as hostile until checked. Nothing is
/// parsed until the index's signature verifies against the key built into QuickMail, every read is
/// capped, a scheme is fetched only by a name the signed index gives, never from another host, and
/// only a file of exactly the signed size and hash is handed on, to <see cref="SoundSchemeFile.Import"/>,
/// which checks it all again.
/// </summary>
public sealed partial class SoundGalleryService : ISoundGalleryService, IDisposable
{
    /// <summary>Where the gallery is published.</summary>
    public const string DefaultLocation = "https://kellylford.github.io/quickmail-sounds/";

    /// <summary>
    /// The public half of the gallery's signing key. The private half lives only in the gallery repo's
    /// <c>gallery-signing</c> environment, which only <c>main</c> can enter.
    /// </summary>
    public const string GalleryPublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEQwiXJpzslRlBsXFp1i5SA+mq+RjX
        omSYj0/L6q6wMmhKLQjZvF5NUTIHNZVBJG4SkaZPgEH0Dfl/BQpvo8otKQ==
        -----END PUBLIC KEY-----
        """;

    internal const string IndexName = "index.signed";
    internal const long MaxIndexBytes = 256 * 1024;
    private const int MaxSignatureLine = 512;
    private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(20);
    private readonly TimeSpan _readTimeout;
    internal const int MaxSchemes = 200;
    internal const int CurrentFormat = 1;
    private const int MaxAuthorLength = 60;
    private const int MaxDescriptionLength = 300;

    private const string Unreachable = "The gallery could not be reached. Check your connection, then choose Reload.";
    private const string NotTrusted = "The gallery's list could not be checked, so it was not used. Try again later.";

    private readonly string _location;
    private readonly bool _isFolder;
    private readonly string _publicKeyPem;
    private readonly HttpClient? _http;

    /// <param name="location">An https URL, or a local folder, holding the gallery. Defaults to <see cref="DefaultLocation"/>.</param>
    /// <param name="publicKeyPem">The key the index must be signed with. Tests pass their own.</param>
    /// <param name="handler">The HTTP handler, for tests. Redirects are never followed.</param>
    /// <param name="readTimeout">How long one whole read may take, body included; 20 seconds unless a test says otherwise.</param>
    public SoundGalleryService(string? location = null, string? publicKeyPem = null, HttpMessageHandler? handler = null, TimeSpan? readTimeout = null)
    {
        _readTimeout = readTimeout ?? DefaultReadTimeout;
        location = string.IsNullOrWhiteSpace(location) ? DefaultLocation : location.Trim();
        _publicKeyPem = publicKeyPem ?? GalleryPublicKeyPem;

        if (Uri.TryCreate(location, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            // Plain http only for a gallery on this computer, which is how a staging copy is tested.
            if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
                throw new ArgumentException("The sound gallery must be an https address or a folder.", nameof(location));
            _location = location.EndsWith('/') ? location : location + "/";
            // A redirect could lead anywhere, so it is refused rather than followed.
            // The read's own timeout (_readTimeout) covers the body too; this one only backs it up.
            _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("QuickMail-SoundGallery/1");
        }
        else if (uri is not null && !uri.IsFile)
        {
            throw new ArgumentException("The sound gallery must be an https address or a folder.", nameof(location));
        }
        else
        {
            _location = Path.GetFullPath(uri?.LocalPath ?? location);
            _isFolder = true;
        }
    }

    public async Task<IReadOnlyList<SoundGalleryEntry>> LoadAsync(CancellationToken ct)
    {
        var signed = await ReadAsync(IndexName, MaxIndexBytes + MaxSignatureLine, ct).ConfigureAwait(false);
        if (!TrySplitSigned(signed, out var signature, out var index) || !Verify(index, signature, _publicKeyPem))
        {
            LogService.Log("Sound gallery: the index's signature did not verify; ignored.");
            throw new SoundGalleryException(NotTrusted);
        }
        return Parse(index);
    }

    /// <summary>Splits index.signed into its signature line and the exact index bytes after it.</summary>
    internal static bool TrySplitSigned(byte[] signed, out byte[] signature, out byte[] index)
    {
        signature = index = [];
        var newline = Array.IndexOf(signed, (byte)'\n', 0, Math.Min(signed.Length, MaxSignatureLine));
        if (newline <= 0 || signed.Length - newline - 1 > MaxIndexBytes) return false;
        signature = signed[..newline];
        index = signed[(newline + 1)..];
        return index.Length > 0;
    }

    /// <summary>The file a scheme must be in: its id and the start of its hash.</summary>
    internal static string FileFor(string id, string sha256) => $"{id}-{sha256[..16]}{SoundSchemeFile.FileExtension}";

    public async Task<string> DownloadAsync(SoundGalleryEntry entry, CancellationToken ct)
    {
        // Only a name this class's own parser accepted, so it can't point anywhere else.
        if (!IsValidId(entry.Id) || !Sha256Pattern().IsMatch(entry.Sha256) || entry.File != FileFor(entry.Id, entry.Sha256)
            || entry.Size <= 0 || entry.Size > SoundSchemeFile.MaxFileBytes)
            throw new SoundGalleryException("That scheme's listing is not valid.");

        var bytes = await ReadAsync(entry.File, entry.Size, ct, exactSize: true).ConfigureAwait(false);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(entry.Sha256)))
        {
            LogService.Log($"Sound gallery: {entry.File} did not match its listed hash; discarded.");
            throw new SoundGalleryException($"The download of {entry.Name} was not the file the gallery lists, so it was not used. Try again later.");
        }

        var dir = Path.Combine(Path.GetTempPath(), "QuickMail-gallery-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, entry.File);
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
            return path;
        }
        catch (Exception ex)
        {
            // Nothing half-written is left behind, whatever went wrong.
            DeleteDownload(path);
            if (ex is IOException or UnauthorizedAccessException)
            {
                LogService.Log("Sound gallery: could not save a download", ex);
                throw new SoundGalleryException("The scheme could not be saved to a temporary folder. " + ex.Message, ex);
            }
            throw;
        }
    }

    /// <summary>Deletes a file <see cref="DownloadAsync"/> returned, and its folder.</summary>
    public static void DeleteDownload(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null && Path.GetFileName(dir).StartsWith("QuickMail-gallery-", StringComparison.Ordinal))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Log("Sound gallery: could not tidy up a download", ex);
        }
    }

    /// <summary>Whether <paramref name="signature"/> (base64 DER ECDSA P-256/SHA-256) signs exactly <paramref name="data"/>.</summary>
    internal static bool Verify(byte[] data, byte[] signature, string publicKeyPem)
    {
        try
        {
            var der = Convert.FromBase64String(Encoding.ASCII.GetString(signature).Trim());
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            return key.VerifyData(data, der, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Reads the signed index. An entry that breaks any rule is dropped, not trusted in part.</summary>
    internal static IReadOnlyList<SoundGalleryEntry> Parse(byte[] index)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(index, new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException ex)
        {
            throw new SoundGalleryException(NotTrusted, ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format)
                || format.ValueKind != JsonValueKind.Number || !format.TryGetInt32(out var version) || version < 1)
                throw new SoundGalleryException(NotTrusted);
            if (version > CurrentFormat)
                throw new SoundGalleryException("The gallery needs a newer version of QuickMail. Update QuickMail and try again.");
            if (!root.TryGetProperty("schemes", out var schemes) || schemes.ValueKind != JsonValueKind.Array)
                throw new SoundGalleryException(NotTrusted);

            var entries = new List<SoundGalleryEntry>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in schemes.EnumerateArray().Take(MaxSchemes))
            {
                if (TryReadEntry(item) is { } entry && ids.Add(entry.Id)) entries.Add(entry);
                else LogService.Log("Sound gallery: dropped an entry that broke the index rules.");
            }
            return entries;
        }
    }

    private static SoundGalleryEntry? TryReadEntry(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        string? Text(string name) =>
            item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var id = Text("id");
        var file = Text("file");
        var sha = Text("sha256");
        var name = SoundSchemeFile.CleanName(Text("name"));
        var author = Clean(Text("author"), MaxAuthorLength);
        var description = Clean(Text("description"), MaxDescriptionLength);
        var license = Clean(Text("license"), 40);
        if (id is null || !IsValidId(id) || sha is null || !Sha256Pattern().IsMatch(sha) || file != FileFor(id, sha)) return null;
        if (author.Length == 0 || license.Length == 0) return null;
        // TryGetInt64 throws for a value that isn't a number, so the kind is checked first.
        if (!item.TryGetProperty("size", out var sizeElement) || sizeElement.ValueKind != JsonValueKind.Number
            || !sizeElement.TryGetInt64(out var size) || size <= 0 || size > SoundSchemeFile.MaxFileBytes) return null;
        var events = item.TryGetProperty("eventsWithSound", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n)
            ? Math.Clamp(n, 0, SoundCatalog.Events.Count) : 0;
        return new SoundGalleryEntry(id, name, author, description, license, file, size, sha, events);
    }

    private static string Clean(string? text, int max)
    {
        var clean = new string((text ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        return SoundSchemeFile.Cut(clean, max);
    }

    internal static bool IsValidId(string id) => IdPattern().IsMatch(id);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();

    /// <summary>Reads one file from the gallery, never more than <paramref name="limit"/> bytes.</summary>
    /// <param name="exactSize">Fail unless it is exactly <paramref name="limit"/> bytes.</param>
    private async Task<byte[]> ReadAsync(string name, long limit, CancellationToken callerToken, bool exactSize = false)
    {
        // A timeout over the whole read, body included: HttpClient.Timeout stops at the headers when the
        // body is streamed, and a connection that stalls after them would otherwise wait for ever.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        timeout.CancelAfter(_readTimeout);
        var ct = timeout.Token;
        try
        {
            if (_isFolder)
            {
                var path = Path.Combine(_location, name);
                await using var file = File.OpenRead(path);
                return await ReadCappedAsync(file, limit, exactSize, ct).ConfigureAwait(false);
            }

            using var response = await _http!.GetAsync(new Uri(new Uri(_location), name), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                LogService.Log($"Sound gallery: {name} answered {(int)response.StatusCode}.");
                throw new SoundGalleryException(Unreachable);
            }
            if (response.Content.Headers.ContentLength is { } length && (length > limit || (exactSize && length != limit)))
                throw new SoundGalleryException(NotTrusted);
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await ReadCappedAsync(stream, limit, exactSize, ct).ConfigureAwait(false);
        }
        catch (SoundGalleryException) { throw; }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex)
        {
            LogService.Log($"Sound gallery: reading {name} timed out", ex);
            throw new SoundGalleryException(Unreachable, ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            LogService.Log($"Sound gallery: reading {name} failed", ex);
            throw new SoundGalleryException(Unreachable, ex);
        }
    }

    // Counted as it is read: a server's stated length is only a claim.
    private static async Task<byte[]> ReadCappedAsync(Stream stream, long limit, bool exactSize, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        for (int read; (read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0;)
        {
            total += read;
            if (total > limit) throw new SoundGalleryException(NotTrusted);
            ms.Write(buffer, 0, read);
        }
        if (exactSize && total != limit) throw new SoundGalleryException(NotTrusted);
        return ms.ToArray();
    }

    public void Dispose() => _http?.Dispose();
}
