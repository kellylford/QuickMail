using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace QuickMail.Services;

/// <summary>
/// Fetches a picture from the web for the reading pane, message tabs and message windows, once the
/// user has asked for pictures from the web (#508). The WebView2 never makes these requests itself:
/// it asks QuickMail's own picture address, and QuickMail fetches the real one here, so what goes
/// out is exactly what is written below and nothing the message or the browser engine adds.
///
/// <list type="bullet">
/// <item>No cookies are sent or kept, and no Referer is sent: a picture server learns the address
/// asked for, the computer's IP address and when, which is what loading a picture necessarily
/// tells it, and nothing that ties one message to another.</item>
/// <item>Only public addresses are contacted. A picture pointing at this computer or the local
/// network (a router's admin page, say) is never requested — including through a redirect, and
/// checked on the address actually connected to, so a name that resolves differently the second
/// time cannot slip past.</item>
/// <item>What comes back is kept only if its bytes are a PNG, JPEG, GIF, WebP or BMP, whatever the
/// server says it is. Never SVG, which can carry script.</item>
/// <item>At most <see cref="MaxPictureBytes"/> per picture, a few at a time, with a time limit.</item>
/// </list>
/// Recently fetched pictures are kept in memory, so going back to a message does not fetch its
/// pictures again. A failure is not remembered: it may be the network, and the next look may work.
/// </summary>
public static class WebPictureFetcher
{
    /// <summary>A picture larger than this is not shown; its description stands in.</summary>
    public const long MaxPictureBytes = 10L * 1024 * 1024;

    private const int MaxRedirects = 5;
    private const long CacheBytes = 32L * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The longest one picture may take, redirects and body included. HttpClient.Timeout covers
    /// only the wait for headers; a server that then sends its body a byte a minute would hold one
    /// of the few fetch slots for ever, and six of them would stop web pictures loading anywhere
    /// until QuickMail restarted (#508 security review).
    /// </summary>
    internal static TimeSpan DownloadDeadline { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A picture fetched from the web: its bytes, and the type they were found to be.</summary>
    public sealed record WebPicture(byte[] Bytes, string ContentType);

    private static readonly SemaphoreSlim Concurrency = new(6);
    private static readonly object Gate = new();
    private static readonly LinkedList<(string Url, WebPicture Picture)> Cache = new();
    private static readonly Dictionary<string, Task<WebPicture?>> InFlight = new(StringComparer.Ordinal);
    private static long _cachedBytes;

    /// <summary>Tests replace the network; null uses the real one.</summary>
    internal static HttpMessageHandler? HandlerOverride { get; set; }

    /// <summary>Tests replace name resolution for the proxy check; null uses DNS.</summary>
    internal static Func<string, CancellationToken, Task<IPAddress[]>>? ResolveOverride { get; set; }

    private static readonly Lazy<HttpClient> RealClient = new(() => new HttpClient(new SocketsHttpHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false, // each hop is checked below
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = ConnectToPublicAddressAsync,
    })
    { Timeout = RequestTimeout });

    private static (HttpMessageHandler Handler, HttpClient Client)? _test;

    private static HttpClient Client
    {
        get
        {
            var handler = HandlerOverride;
            if (handler is null) return RealClient.Value;
            // One field holding both, so a fetch on another thread never pairs the new handler
            // with the previous client.
            var test = _test;
            if (test is not { } t || !ReferenceEquals(t.Handler, handler))
            {
                t = (handler, new HttpClient(handler, disposeHandler: false) { Timeout = RequestTimeout });
                _test = t;
            }
            return t.Client;
        }
    }

    /// <summary>
    /// The picture at <paramref name="url"/>, or null when it cannot be had or is not a picture.
    /// Never throws. <paramref name="cancellationToken"/> only stops this caller waiting; a fetch
    /// already under way finishes for anything else showing the same picture.
    /// </summary>
    public static async Task<WebPicture?> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        Task<WebPicture?> task;
        lock (Gate)
        {
            for (var node = Cache.First; node != null; node = node.Next)
            {
                if (node.Value.Url != url) continue;
                Cache.Remove(node);
                Cache.AddFirst(node); // most recently used
                return node.Value.Picture;
            }
            if (!InFlight.TryGetValue(url, out task!))
            {
                task = Task.Run(() => FetchAndRememberAsync(url));
                if (!task.IsCompleted) InFlight[url] = task;
            }
        }
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<WebPicture?> FetchAndRememberAsync(string url)
    {
        try
        {
            var picture = await DownloadAsync(url).ConfigureAwait(false);
            if (picture is not null) Remember(url, picture);
            return picture;
        }
        catch (Exception ex)
        {
            // The address is the sender's and may identify the recipient; only the host is logged.
            LogService.Debug($"WebPictureFetcher: {HostOf(url)}: {ex.GetType().Name}");
            return null;
        }
        finally
        {
            lock (Gate) InFlight.Remove(url);
        }
    }

    private static async Task<WebPicture?> DownloadAsync(string url)
    {
        await Concurrency.WaitAsync().ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(DownloadDeadline);
        var token = deadline.Token;
        try
        {
            var current = url;
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                if (!Uri.TryCreate(current, UriKind.Absolute, out var uri) || !IsFetchable(uri))
                    return null;
                if (!await ProxiedTargetIsPublicAsync(uri).ConfigureAwait(false))
                    return null;

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Accept.ParseAdd("image/webp,image/png,image/jpeg,image/gif,image/*;q=0.8");
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);

                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    if (response.Headers.Location is not { } location) return null;
                    var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    // Never from encrypted to plain: the address often carries who the message was for.
                    if (uri.Scheme == Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttps) return null;
                    current = next.AbsoluteUri;
                    continue;
                }
                if (!response.IsSuccessStatusCode) return null;
                if (response.Content.Headers.ContentLength > MaxPictureBytes) return null;

                var bytes = await ReadCappedAsync(response.Content, token).ConfigureAwait(false);
                if (bytes is null) return null;
                return PictureType(bytes) is { } type ? new WebPicture(bytes, type) : null;
            }
            return null; // too many redirects
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            LogService.Debug($"WebPictureFetcher: {HostOf(url)}: gave up after {DownloadDeadline.TotalSeconds:0} s");
            return null;
        }
        finally
        {
            Concurrency.Release();
        }
    }

    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxPictureBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>An http or https address with a host and no user name or password.</summary>
    internal static bool IsFetchable(Uri uri) =>
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && !string.IsNullOrEmpty(uri.Host)
        && string.IsNullOrEmpty(uri.UserInfo);

    /// <summary>
    /// The picture type <paramref name="bytes"/> actually are, from their first bytes, or null for
    /// anything else — whatever type the server claimed.
    /// </summary>
    internal static string? PictureType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return "image/png";
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])) return "image/jpeg";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return "image/gif";
        if (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        if (bytes.Length >= 26 && bytes.StartsWith("BM"u8)) return "image/bmp";
        return null;
    }

    // ── Public addresses only ───────────────────────────────────────────────

    /// <summary>
    /// Every connection this client makes: resolves the name and connects only to a public
    /// address. Checking the address actually connected to, rather than resolving once beforehand,
    /// is what stops a name that answers with a public address first and a private one second.
    /// A connection to the system's configured proxy is the exception: the proxy is the user's own
    /// choice, and <see cref="ProxiedTargetIsPublicAsync"/> has already checked where it will go.
    /// </summary>
    private static async ValueTask<Stream> ConnectToPublicAddressAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var target = context.DnsEndPoint;
        var addresses = IPAddress.TryParse(target.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(target.Host, cancellationToken).ConfigureAwait(false);

        var isProxy = ProxyFor(context.InitialRequestMessage.RequestUri) is { } proxy
                      && string.Equals(proxy.Host, target.Host, StringComparison.OrdinalIgnoreCase);

        Exception? last = null;
        foreach (var address in addresses)
        {
            if (!isProxy && !IsPublic(address)) continue;
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, target.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                last = ex;
            }
        }
        throw last ?? new HttpRequestException("Pictures are fetched from public addresses only.");
    }

    private static Uri? ProxyFor(Uri? uri)
    {
        if (uri is null) return null;
        try
        {
            var proxy = HttpClient.DefaultProxy;
            return proxy.IsBypassed(uri) ? null : proxy.GetProxy(uri);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// When the request goes through a proxy, the connection check above sees only the proxy; the
    /// address the proxy will reach is checked here instead, by resolving it the same way.
    /// Direct requests pass straight through to the connection check.
    /// <para>Weaker than the direct check: the proxy resolves the name again for itself, so a name
    /// that answers differently the second time reaches whatever the proxy can reach. A proxy is
    /// the user's (or their organisation's) own choice and decides for itself what it will
    /// fetch; this check only stops the plain cases (#508 security review).</para>
    /// </summary>
    private static async Task<bool> ProxiedTargetIsPublicAsync(Uri uri)
    {
        if (HandlerOverride is null && ProxyFor(uri) is null) return true;
        if (HandlerOverride is not null && ResolveOverride is null) return true;
        try
        {
            var addresses = IPAddress.TryParse(uri.Host, out var literal)
                ? [literal]
                : await (ResolveOverride ?? ((h, ct) => Dns.GetHostAddressesAsync(h, ct)))(uri.DnsSafeHost, CancellationToken.None)
                    .ConfigureAwait(false);
            return addresses.Length > 0 && Array.TrueForAll(addresses, IsPublic);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// False for this computer, the local network, and addresses no picture server has:
    /// loopback, private ranges, link-local (which includes cloud metadata services), shared
    /// carrier space, multicast, and the reserved blocks.
    /// </summary>
    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0
                     || b[0] == 10
                     || b[0] == 127
                     || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                     || (b[0] == 169 && b[1] == 254)
                     || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                     || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                     || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 198 && (b[1] == 18 || b[1] == 19))
                     || b[0] >= 224);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None)
                || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
                return false;
            var b = address.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false; // unique local, fc00::/7
            // NAT64 (64:ff9b::/96) carries an IPv4 address: judge that one.
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0)
                return IsPublic(new IPAddress(b.AsSpan(12, 4)));
            return true;
        }
        return false;
    }

    // ── Cache ───────────────────────────────────────────────────────────────

    private static void Remember(string url, WebPicture picture)
    {
        lock (Gate)
        {
            Cache.AddFirst((url, picture));
            _cachedBytes += picture.Bytes.Length;
            while (_cachedBytes > CacheBytes && Cache.Last is { } oldest)
            {
                _cachedBytes -= oldest.Value.Picture.Bytes.Length;
                Cache.RemoveLast();
            }
        }
    }

    /// <summary>Forgets every remembered picture; for tests.</summary>
    internal static void ClearCache()
    {
        lock (Gate)
        {
            Cache.Clear();
            _cachedBytes = 0;
        }
    }

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "(bad address)";
}
