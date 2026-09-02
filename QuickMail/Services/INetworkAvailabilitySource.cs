using System;
using System.Net.NetworkInformation;

namespace QuickMail.Services;

/// <summary>
/// The machine-level "is there a network at all?" signal, behind an interface so the connectivity
/// service can be tested without a network card.
/// </summary>
public interface INetworkAvailabilitySource : IDisposable
{
    bool IsAvailable { get; }

    /// <summary>Raised on a ThreadPool thread whenever Windows reports the network came up or went away.</summary>
    event Action<bool>? AvailabilityChanged;
}

/// <summary>
/// Wraps <see cref="NetworkChange.NetworkAvailabilityChanged"/> and
/// <see cref="NetworkInterface.GetIsNetworkAvailable"/>. Note what this does and does not say: "up"
/// means some interface other than loopback has a link, which a captive portal, a VPN adapter or a
/// virtual switch will happily report with no route to the mail server behind it. That is why the
/// connectivity service also listens to what real operations report.
/// </summary>
public sealed class NetworkChangeAvailabilitySource : INetworkAvailabilitySource
{
    private bool _disposed;

    public NetworkChangeAvailabilitySource()
    {
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
    }

    public bool IsAvailable => NetworkInterface.GetIsNetworkAvailable();

    public event Action<bool>? AvailabilityChanged;

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
        => AvailabilityChanged?.Invoke(e.IsAvailable);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
    }
}
