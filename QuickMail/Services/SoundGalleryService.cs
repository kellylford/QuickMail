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
/// <param name="File">Its .qmsounds file in the gallery, always <c>&lt;Id&gt;.qmsounds</c>.</param>
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
/// Reads the sound scheme gallery: <c>index.json</c>, signed into <c>index.json.sig</c>, and one
/// <c>.qmsounds</c> file per scheme, all in one place: GitHub Pages, or a folder or URL given with
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

    internal const string IndexName = "index.json";
    internal const string SignatureName = "index.json.sig";
    internal const long MaxIndexBytes = 256 * 1024;
    internal const long MaxSignatureBytes = 1024;
    internal const int MaxSchemes = 200;
    internal const int CurrentFormat = 1;
    private const int MaxAuthorLength = 60;
    private const int MaxDescriptionLength = 300;

    private const string Unreachable = "The gallery could not be reached. Check your connection and try again.";
    private const string NotTrusted = "The gallery's list could not be checked, so it was not used. Try again later.";

    private readonly string _location;
    private readonly bool _isFolder;
    private readonly string _publicKeyPem;
    private readonly HttpClient? _http;

    /// <param name="location">An https URL, or a local folder, holding the gallery. Defaults to <see cref="DefaultLocation"/>.</param>
    /// <param name="publicKeyPem">The key the index must be signed with. Tests pass their own.</param>
    /// <param name="handler">The HTTP handler, for tests. Redirects are never followed.</param>
    public SoundGalleryService(string? location = null, string? publicKeyPem = null, HttpMessageHandler? handler = null)
    {
        location = string.IsNullOrWhiteSpace(location) ? DefaultLocation : location.Trim();
        _publicKeyPem = publicKeyPem ?? GalleryPublicKeyPem;

        if (Uri.TryCreate(location, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            // Plain http only for a gallery on this computer, which is how a staging copy is tested.
            if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
                throw new ArgumentException("The sound gallery must be an https address or a folder.", nameof(location));
            _location = location.EndsWith('/') ? location : location + "/";
            // A redirect could lead anywhere, so it is refused rather than followed.
            _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
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
        var index = await ReadAsync(IndexName, MaxIndexBytes, ct).ConfigureAwait(false);
        var signature = await ReadAsync(SignatureName, MaxSignatureBytes, ct).ConfigureAwait(false);
        if (!Verify(index, signature, _publicKeyPem))
        {
            LogService.Log("Sound gallery: the index's signature did not verify; ignored.");
            throw new SoundGalleryException(NotTrusted);
        }
        return Parse(index);
    }

    public async Task<string> DownloadAsync(SoundGalleryEntry entry, CancellationToken ct)
    {
        // Only a name this class's own parser accepted, so it can't point anywhere else.
        if (!IsValidId(entry.Id) || entry.File != entry.Id + SoundSchemeFile.FileExtension
            || entry.Size <= 0 || entry.Size > SoundSchemeFile.MaxFileBytes || !Sha256Pattern().IsMatch(entry.Sha256))
            throw new SoundGalleryException("That scheme's listing is not valid.");

        var bytes = await ReadAsync(entry.File, entry.Size, ct, exactSize: true).ConfigureAwait(false);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(entry.Sha256)))
        {
            LogService.Log($"Sound gallery: {entry.File} did not match its listed hash; discarded.");
            throw new SoundGalleryException($"The download of {entry.Name} was not the file the gallery lists, so it was not used. Try again later.");
        }

        var dir = Path.Combine(Path.GetTempPath(), "QuickMail-gallery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, entry.File);
        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
        return path;
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
        if (id is null || !IsValidId(id) || file != id + SoundSchemeFile.FileExtension) return null;
        if (sha is null || !Sha256Pattern().IsMatch(sha)) return null;
        if (author.Length == 0 || license.Length == 0) return null;
        if (!item.TryGetProperty("size", out var sizeElement) || !sizeElement.TryGetInt64(out var size)
            || size <= 0 || size > SoundSchemeFile.MaxFileBytes) return null;
        var events = item.TryGetProperty("eventsWithSound", out var e) && e.TryGetInt32(out var n)
            ? Math.Clamp(n, 0, SoundCatalog.Events.Count) : 0;
        return new SoundGalleryEntry(id, name, author, description, license, file, size, sha, events);
    }

    private static string Clean(string? text, int max)
    {
        var clean = new string((text ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > max ? clean[..max].TrimEnd() : clean;
    }

    internal static bool IsValidId(string id) => IdPattern().IsMatch(id);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();

    /// <summary>Reads one file from the gallery, never more than <paramref name="limit"/> bytes.</summary>
    /// <param name="exactSize">Fail unless it is exactly <paramref name="limit"/> bytes.</param>
    private async Task<byte[]> ReadAsync(string name, long limit, CancellationToken ct, bool exactSize = false)
    {
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
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
