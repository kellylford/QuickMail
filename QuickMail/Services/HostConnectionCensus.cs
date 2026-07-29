using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace QuickMail.Services;

/// <summary>
/// Tracks how many live IMAP sockets exist per host, and which accounts own them.
///
/// Our connection cap is per <em>account</em>; a server's cap is per <em>user+IP</em> and, on
/// shared hosting, per IP overall. Two accounts on different hostnames can resolve to the same
/// server — in the profile that reported these disconnects, <c>mail.kellford.com</c> and
/// <c>mail.theideaplace.net</c> both resolve to 50.87.253.68 — so the account-level cap says
/// "6" while the server sees 12 command connections plus 2 held IDLE sockets from one client.
///
/// This class does not enforce anything. It exists so the journal can answer, for every connect
/// attempt: how many sockets did we already have open to this host, and to its IP, and which
/// accounts were they for. If per-IP exhaustion is the cause, failures will cluster at a
/// consistent count across both accounts sharing an address. If it is not, the pattern won't
/// be there and we can stop suspecting it.
/// </summary>
public static class HostConnectionCensus
{
    // host -> accountId -> live socket count
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, int>> _byHost =
        new(StringComparer.OrdinalIgnoreCase);

    // host -> resolved address list, cached for the process lifetime. DNS is resolved once per
    // host on first use so the census never adds a lookup to the connect path.
    private static readonly ConcurrentDictionary<string, string> _resolved =
        new(StringComparer.OrdinalIgnoreCase);

    // Which host/account each live client belongs to, so a client can be released without the
    // release site needing to know either. Clients are disposed from static helpers that have no
    // account context, and double-releasing would corrupt the count — the table makes release
    // idempotent (the entry is removed on first release).
    //
    // LIMITATION, stated plainly because this number is read as evidence: the count is decremented
    // only by Released(), which every disposal path funnels through (DisposeClient). A client that
    // were somehow collected WITHOUT being disposed would take its table entry with it and leave
    // the counter high forever, and an inflated count is exactly the fabricated exhaustion evidence
    // this class exists to detect. MailKit's ImapClient is IDisposable and the pool disposes every
    // client it owns, so that path should not occur — but treat a census that looks impossibly high
    // as suspect rather than as proof, and cross-check against the pool= figure on the same line.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Registration> _registry = new();

    private sealed class Registration
    {
        public required string Host { get; init; }
        public required Guid AccountId { get; init; }
        public int Released;   // 0 = live, 1 = already released
    }

    /// <summary>Records that a socket to <paramref name="host"/> was opened for an account.</summary>
    public static void Opened(string host, Guid accountId, object? client = null)
    {
        if (string.IsNullOrWhiteSpace(host)) return;
        var perAccount = _byHost.GetOrAdd(host, _ => new ConcurrentDictionary<Guid, int>());
        perAccount.AddOrUpdate(accountId, 1, (_, n) => n + 1);

        if (client != null)
        {
            // Overwrite defensively: a recycled client object must not keep a stale registration.
            _registry.Remove(client);
            _registry.Add(client, new Registration { Host = host, AccountId = accountId });
        }
    }

    /// <summary>Records that a socket to <paramref name="host"/> was closed for an account.</summary>
    public static void Closed(string host, Guid accountId)
    {
        if (string.IsNullOrWhiteSpace(host)) return;
        if (!_byHost.TryGetValue(host, out var perAccount)) return;
        perAccount.AddOrUpdate(accountId, 0, (_, n) => Math.Max(0, n - 1));
    }

    /// <summary>
    /// Releases whatever socket <paramref name="client"/> was registered for. Safe to call more
    /// than once, and safe for clients that were never registered (a connect that failed before
    /// the socket opened).
    /// </summary>
    public static void Released(object? client)
    {
        if (client == null) return;
        if (!_registry.TryGetValue(client, out var reg)) return;
        if (System.Threading.Interlocked.Exchange(ref reg.Released, 1) != 0) return;

        _registry.Remove(client);
        Closed(reg.Host, reg.AccountId);
    }

    /// <summary>Total live sockets to a host across all accounts.</summary>
    public static int LiveCount(string host) =>
        _byHost.TryGetValue(host, out var perAccount) ? perAccount.Values.Sum() : 0;

    /// <summary>
    /// The cached resolved address for a host, resolving it on first call. Returns "unresolved"
    /// rather than throwing — this is diagnostic colour, never a gate on connecting.
    /// </summary>
    public static string ResolvedAddress(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return "-";
        return _resolved.GetOrAdd(host, h =>
        {
            try
            {
                var addresses = Dns.GetHostAddresses(h);
                return addresses.Length == 0
                    ? "unresolved"
                    : string.Join(",", addresses.Select(a => a.ToString()));
            }
            catch (Exception ex)
            {
                return $"unresolved({ex.GetType().Name})";
            }
        });
    }

    /// <summary>
    /// Every other host we currently hold sockets to that shares an IP address with
    /// <paramref name="host"/>. Empty when nothing else resolves to the same server.
    /// </summary>
    public static IReadOnlyList<string> HostsSharingAddress(string host)
    {
        var mine = ResolvedAddress(host);
        if (mine.StartsWith("unresolved", StringComparison.Ordinal) || mine == "-")
            return Array.Empty<string>();

        var mineSet = mine.Split(',');
        return _byHost.Keys
            .Where(other => !string.Equals(other, host, StringComparison.OrdinalIgnoreCase))
            .Where(other => ResolvedAddress(other).Split(',').Intersect(mineSet, StringComparer.Ordinal).Any())
            .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// A one-line census for the journal: total sockets to this host, the resolved address, the
    /// combined total across every host sharing that address, and who the neighbours are.
    /// </summary>
    public static string Describe(string host)
    {
        var live    = LiveCount(host);
        var address = ResolvedAddress(host);
        var shared  = HostsSharingAddress(host);

        if (shared.Count == 0)
            return $"hostSockets={live} ip={address}";

        var sharedTotal = live + shared.Sum(LiveCount);
        return $"hostSockets={live} ip={address} " +
               $"sharedIpSockets={sharedTotal} sharedWith=[{string.Join(",", shared)}]";
    }

    /// <summary>Snapshot of every tracked host and its live socket count, for the report.</summary>
    public static IReadOnlyList<string> SnapshotLines() =>
        _byHost
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"{kv.Key} — sockets={kv.Value.Values.Sum()} ip={ResolvedAddress(kv.Key)}")
            .ToList();

    /// <summary>Test hook.</summary>
    internal static void ResetForTests()
    {
        _byHost.Clear();
        _resolved.Clear();
    }
}
