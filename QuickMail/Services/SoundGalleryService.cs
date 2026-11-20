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
/// <param name="EventsWithSound">How many events it gives a sound, or null when the list doesn't say.</param>
public sealed record SoundGalleryEntry(
    string Id, string Name, string Author, string Description, string License,
    string File, long Size, string Sha256, int? EventsWithSound)
{
    /// <summary>What the gallery's list says for it, worded as The Chat Place words it.</summary>
    public string AccessibleName =>
        $"{Name}, by {Author}."
        + (EventsWithSound is { } count ? $" {count} of {SoundCatalog.Events.Count} events have a sound." : string.Empty);

    /// <summary>The row's help text: its description and licence.</summary>
    public string HelpText => (Description.Length > 0 ? Description + " " : string.Empty) + $"Licence: {License}.";

    public override string ToString() => AccessibleName;
}

/// <summary>What sort of trouble the gallery had, as SCHEMES.md sorts it.</summary>
public enum SoundGalleryProblem
{
    /// <summary>Couldn't connect, timed out, not a 200 answer, or a damaged answer.</summary>
    Unreachable,

    /// <summary>The signature or the list is wrong, or the list is another app's.</summary>
    Untrusted,

    /// <summary>The list is in a newer format than this QuickMail reads.</summary>
    NeedsUpdate,

    /// <summary>A download that isn't the file the list names.</summary>
    NotTheListedFile,

    /// <summary>Anything else, such as a download that couldn't be saved.</summary>
    Other,
}

/// <summary>The gallery couldn't be read, or a scheme from it couldn't be fetched; the message is for the person.</summary>
public sealed class SoundGalleryException(string message, Exception? inner = null, SoundGalleryProblem kind = SoundGalleryProblem.Other)
    : Exception(message, inner)
{
    public SoundGalleryException(string message, SoundGalleryProblem kind) : this(message, null, kind) { }

    public SoundGalleryProblem Kind { get; } = kind;
}

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
    public const string DefaultLocation = "https://theideaplace.github.io/soundgallery/quickmail/";

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

    internal const string Unreachable = "The gallery could not be reached. Check your connection, then choose Reload.";
    internal const string NotTrusted = "The gallery's list could not be checked, so it was not used. Try again later.";
    internal const string NeedsUpdate = "The gallery needs a newer version of QuickMail. Update QuickMail and try again.";

    private static SoundGalleryException UnreachableError(Exception? inner = null) => new(Unreachable, inner, SoundGalleryProblem.Unreachable);

    private static SoundGalleryException Untrusted(Exception? inner = null) => new(NotTrusted, inner, SoundGalleryProblem.Untrusted);

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

        // Never a network share, which would hand that host your Windows sign-in, and never an
        // address with a user name in it (SCHEMES.md, "The gallery").
        if (location.Replace('\\', '/').StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("The sound gallery can't be on a network share.", nameof(location));
        Uri.TryCreate(location, UriKind.Absolute, out var uri);
        if (uri is not null && (uri.UserInfo.Length > 0 || (uri.IsFile && (uri.IsUnc || !(uri.Host.Length == 0 || uri.IsLoopback)))))
            throw new ArgumentException("The sound gallery must be an https address or a folder on this computer.", nameof(location));

        if (uri is not null && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
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
        return ReadIndex(signed, _publicKeyPem);
    }

    /// <summary>
    /// The schemes a whole <c>index.signed</c> lists for QuickMail, once its signature verifies.
    /// Throws <see cref="SoundGalleryException"/>: untrusted, or needing a newer QuickMail.
    /// </summary>
    internal static IReadOnlyList<SoundGalleryEntry> ReadIndex(byte[] signed, string publicKeyPem)
    {
        if (!TrySplitSigned(signed, out var signature, out var index) || !Verify(index, signature, publicKeyPem))
        {
            LogService.Log("Sound gallery: the index's signature did not verify; ignored.");
            throw Untrusted();
        }
        return Parse(index);
    }

    /// <summary>Splits index.signed into its signature line and the exact index bytes after it.</summary>
    internal static bool TrySplitSigned(byte[] signed, out byte[] signature, out byte[] index)
    {
        signature = index = [];
        // The newline is one of the first 512 bytes, and is "\n" alone: a "\r" before it isn't base64,
        // so Verify refuses it.
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
        if (!IsValidId(entry.Id) || !IsValidSha256(entry.Sha256) || entry.File != FileFor(entry.Id, entry.Sha256)
            || entry.Size <= 0 || entry.Size > SoundSchemeFile.MaxFileBytes)
            throw new SoundGalleryException("That scheme's listing is not valid.");

        var notTheFile = new SoundGalleryException(
            $"The download of {entry.Name} was not the file the gallery lists, so it was not used. Try again later.",
            SoundGalleryProblem.NotTheListedFile);
        byte[] bytes;
        try
        {
            bytes = await ReadAsync(entry.File, entry.Size, ct, exactSize: true).ConfigureAwait(false);
        }
        catch (SoundGalleryException ex) when (ex.Kind == SoundGalleryProblem.Untrusted)
        {
            // Larger or smaller than listed.
            LogService.Log($"Sound gallery: {entry.File} was not its listed size; discarded.");
            throw notTheFile;
        }
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(entry.Sha256)))
        {
            LogService.Log($"Sound gallery: {entry.File} did not match its listed hash; discarded.");
            throw notTheFile;
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
        // Base64 and nothing else on the line: Convert.FromBase64String would skip spaces, tabs and
        // line breaks, which SCHEMES.md doesn't allow.
        if (signature.Length == 0 || signature.Any(b => !(char.IsAsciiLetterOrDigit((char)b) || b is (byte)'+' or (byte)'/' or (byte)'=')))
            return false;
        try
        {
            var der = Convert.FromBase64String(Encoding.ASCII.GetString(signature));
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            return key.VerifyData(data, der, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the signed index, as SCHEMES.md "index.signed" sets out: strict JSON with no byte-order
    /// mark, a whole-number format, and QuickMail's own list. One key signs every app's list, so
    /// <c>app</c> is what stops QuickMail from taking another app's. An entry that breaks any rule is
    /// dropped, not trusted in part.
    /// </summary>
    internal static IReadOnlyList<SoundGalleryEntry> Parse(byte[] index)
    {
        JsonDocument doc;
        try
        {
            doc = SoundSchemeFile.ParseStrictJson(index, allowBom: false);
        }
        catch (JsonException ex)
        {
            throw Untrusted(ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Untrusted();
            var format = SoundSchemeFile.Property(root, "format") is { } f ? SoundSchemeFile.WholeNumber(f) : null;
            if (format is not { } version || version < 1) throw Untrusted();
            if (version > CurrentFormat) throw new SoundGalleryException(NeedsUpdate, SoundGalleryProblem.NeedsUpdate);
            if (SoundSchemeFile.Property(root, "app") is not { ValueKind: JsonValueKind.String } app
                || SoundSchemeFile.JsonText(app) != SoundSchemeFile.AppId)
            {
                LogService.Log("Sound gallery: the index is not QuickMail's; ignored.");
                throw Untrusted();
            }
            if (SoundSchemeFile.Property(root, "schemes") is not { ValueKind: JsonValueKind.Array } schemes) throw Untrusted();

            var entries = new List<SoundGalleryEntry>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
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
        JsonElement? Value(string name) => SoundSchemeFile.Property(item, name);
        string? Text(string name) => Value(name) is { } v ? SoundSchemeFile.JsonText(v) : null;

        var id = Text("id");
        var file = Text("file");
        var sha = Text("sha256");
        if (id is null || !IsValidId(id) || sha is null || !IsValidSha256(sha) || file != FileFor(id, sha)) return null;
        if (Value("size") is not { } sizeElement || SoundSchemeFile.WholeNumber(sizeElement) is not { } size
            || size < 1 || size > SoundSchemeFile.MaxFileBytes) return null;
        var name = SoundSchemeFile.CleanText(Text("name"), SoundCatalog.MaxSchemeNameLength);
        var author = SoundSchemeFile.CleanText(Text("author"), SoundSchemeFile.MaxAuthorLength);
        var license = SoundSchemeFile.CleanText(Text("license"), SoundSchemeFile.MaxLicenseLength);
        if (name.Length == 0 || author.Length == 0 || license.Length == 0) return null;
        var description = SoundSchemeFile.CleanText(Text("description"), SoundSchemeFile.MaxDescriptionLength);
        int? events = Value("eventsWithSound") is { } e && SoundSchemeFile.WholeNumber(e) is { } n
            ? Math.Clamp(n, 0, SoundCatalog.Events.Count) : null;
        return new SoundGalleryEntry(id, name, author, description, license, file, size, sha, events);
    }

    /// <summary>A gallery id: a lowercase letter or digit, then up to 39 more or hyphens. Matches the whole text, so a trailing newline can't pass.</summary>
    internal static bool IsValidId(string id) => IdPattern().IsMatch(id);

    /// <summary>A SHA-256 as 64 lowercase hex digits, the whole text.</summary>
    internal static bool IsValidSha256(string sha) => Sha256Pattern().IsMatch(sha);

    // \z, not $: in .NET, $ also matches before a final newline.
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,39}\z")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[0-9a-f]{64}\z")]
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
                throw UnreachableError();
            }
            if (response.Content.Headers.ContentLength is { } length && (length > limit || (exactSize && length != limit)))
                throw Untrusted();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await ReadCappedAsync(stream, limit, exactSize, ct).ConfigureAwait(false);
        }
        catch (SoundGalleryException) { throw; }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex)
        {
            LogService.Log($"Sound gallery: reading {name} timed out", ex);
            throw UnreachableError(ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            LogService.Log($"Sound gallery: reading {name} failed", ex);
            throw UnreachableError(ex);
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
            if (total > limit) throw Untrusted();
            ms.Write(buffer, 0, read);
        }
        if (exactSize && total != limit) throw Untrusted();
        return ms.ToArray();
    }

    public void Dispose() => _http?.Dispose();
}
