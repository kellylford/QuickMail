using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace QuickMail.IntegrationTests;

/// <summary>
/// Fault-injecting TCP relay for connection-handling tests (live-content testing plan §4.4).
/// Listens on an ephemeral 127.0.0.1 port and forwards byte streams to a target (GreenMail).
/// Point an account's ImapPort at <see cref="Port"/>, then use <see cref="KillActiveConnections"/>
/// to sever every in-flight connection mid-session — the deterministic stand-in for a server
/// dropping a pooled or IDLE connection, which is the trigger for the #311/#268/#278/#313/#314
/// family of regressions. <see cref="TotalAccepted"/> counts every connection ever accepted, so a
/// test can also assert that connections were NOT re-established (the #126 watcher-stability
/// property).
/// </summary>
public sealed class TcpRelay : IDisposable
{
    private readonly TcpListener _listener;
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, (TcpClient Client, TcpClient Server)> _live = new();
    private int _nextId;
    private int _totalAccepted;

    public TcpRelay(string targetHost, int targetPort)
    {
        _targetHost = targetHost;
        _targetPort = targetPort;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    /// <summary>The ephemeral local port tests point their account's ImapPort at.</summary>
    public int Port { get; }

    /// <summary>Connections accepted over the relay's lifetime (never decremented).</summary>
    public int TotalAccepted => Volatile.Read(ref _totalAccepted);

    /// <summary>Connections currently being relayed.</summary>
    public int LiveConnectionCount => _live.Count;

    /// <summary>
    /// Abruptly closes both sides of every in-flight connection. The listener stays up, so
    /// clients can reconnect — this simulates a server-side drop, not an outage.
    /// </summary>
    public void KillActiveConnections()
    {
        foreach (var (id, pair) in _live.ToArray())
        {
            _live.TryRemove(id, out _);
            try { pair.Client.Close(); } catch { }
            try { pair.Server.Close(); } catch { }
        }
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                Interlocked.Increment(ref _totalAccepted);
                _ = RelayAsync(client);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task RelayAsync(TcpClient client)
    {
        var id = Interlocked.Increment(ref _nextId);
        var server = new TcpClient();
        try
        {
            await server.ConnectAsync(_targetHost, _targetPort, _cts.Token);
            _live[id] = (client, server);

            var upstream   = PumpAsync(client, server);
            var downstream = PumpAsync(server, client);
            await Task.WhenAny(upstream, downstream);
        }
        catch { /* torn-down connections are the point of this class */ }
        finally
        {
            _live.TryRemove(id, out _);
            try { client.Close(); } catch { }
            try { server.Close(); } catch { }
        }
    }

    private async Task PumpAsync(TcpClient from, TcpClient to)
    {
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var read = await from.GetStream().ReadAsync(buffer, _cts.Token);
                if (read == 0) return;
                await to.GetStream().WriteAsync(buffer.AsMemory(0, read), _cts.Token);
            }
        }
        catch { /* closed/killed — RelayAsync's finally tears the pair down */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        KillActiveConnections();
        _cts.Dispose();
    }
}
