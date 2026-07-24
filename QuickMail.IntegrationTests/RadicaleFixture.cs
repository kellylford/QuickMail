using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace QuickMail.IntegrationTests;

/// <summary>
/// Shared fixture for tests that talk to a local Radicale server (CalDAV + CardDAV, port 5232).
/// Same lifecycle contract as <see cref="GreenMailFixture"/>: the server is an external process
/// (scripts/start-test-servers.ps1 or the CI integration job); absent server means dynamic skip
/// locally and hard failure under QUICKMAIL_IT_SERVERS=1.
///
/// Radicale runs with auth-type 'none': any username/password authenticates, and each user owns
/// the /&lt;user&gt;/ hierarchy, so tests invent unique per-run users. Collections are created
/// explicitly (MKCALENDAR / extended MKCOL) via <see cref="CreateCalendarAsync"/> and
/// <see cref="CreateAddressbookAsync"/>.
/// </summary>
public sealed class RadicaleFixture : IDisposable
{
    public const string Host = "127.0.0.1";
    public const int Port = 5232;
    public static string ServerUrl => $"http://{Host}:{Port}/";

    /// <summary>Password for test users — arbitrary, since Radicale auth is 'none'.</summary>
    public const string Password = "test";

    private readonly bool _serverAvailable;
    private readonly HttpClient _http = new();

    public RadicaleFixture()
    {
        _serverAvailable = IsPortOpen(Host, Port);
    }

    public void RequireServer()
    {
        if (_serverAvailable)
            return;

        if (Environment.GetEnvironmentVariable("QUICKMAIL_IT_SERVERS") == "1")
            throw new InvalidOperationException(
                $"QUICKMAIL_IT_SERVERS=1 but Radicale is not reachable on {Host}:{Port}. " +
                "The CI harness failed to start; see the radicale log artifact.");

        Assert.Skip(
            $"Radicale is not running on {Host}:{Port}. " +
            "Start it with scripts/start-test-servers.ps1 (see docs/TESTING-INTEGRATION.md).");
    }

    /// <summary>Unique per-run username; owns the /{user}/ collection hierarchy.</summary>
    public static string CreateUser(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    /// <summary>Creates a calendar collection at /{user}/{name}/ and returns its URL.</summary>
    public async Task<string> CreateCalendarAsync(string user, string name, CancellationToken ct)
    {
        var url = $"{ServerUrl}{user}/{name}/";
        using var request = new HttpRequestMessage(new HttpMethod("MKCALENDAR"), url);
        Authorize(request, user);
        using var response = await _http.SendAsync(request, ct);
        Assert.True(response.IsSuccessStatusCode, $"MKCALENDAR {url} failed: {(int)response.StatusCode}");
        return url;
    }

    /// <summary>Creates an addressbook collection at /{user}/{name}/ (extended MKCOL) and returns its URL.</summary>
    public async Task<string> CreateAddressbookAsync(string user, string name, CancellationToken ct)
    {
        var url = $"{ServerUrl}{user}/{name}/";
        using var request = new HttpRequestMessage(new HttpMethod("MKCOL"), url);
        Authorize(request, user);
        request.Content = new StringContent("""
            <?xml version="1.0" encoding="utf-8"?>
            <D:mkcol xmlns:D="DAV:" xmlns:CR="urn:ietf:params:xml:ns:carddav">
              <D:set><D:prop>
                <D:resourcetype><D:collection/><CR:addressbook/></D:resourcetype>
              </D:prop></D:set>
            </D:mkcol>
            """, Encoding.UTF8, "application/xml");
        using var response = await _http.SendAsync(request, ct);
        Assert.True(response.IsSuccessStatusCode, $"MKCOL (addressbook) {url} failed: {(int)response.StatusCode}");
        return url;
    }

    /// <summary>PUTs a raw resource (ics/vcf body) into a collection.</summary>
    public async Task PutResourceAsync(string resourceUrl, string body, string mediaType, string user, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, resourceUrl);
        Authorize(request, user);
        request.Content = new StringContent(body, Encoding.UTF8, mediaType);
        using var response = await _http.SendAsync(request, ct);
        Assert.True(response.IsSuccessStatusCode, $"PUT {resourceUrl} failed: {(int)response.StatusCode}");
    }

    private static void Authorize(HttpRequestMessage request, string user) =>
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{user}:{Password}")));

    private static bool IsPortOpen(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}

[CollectionDefinition(Name)]
public class RadicaleCollection : ICollectionFixture<RadicaleFixture>
{
    public const string Name = "Radicale";
}
