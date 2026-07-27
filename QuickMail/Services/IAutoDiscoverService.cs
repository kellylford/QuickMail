using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Finds IMAP/SMTP settings for an email address so the user does not have to type hosts, ports,
/// and SSL modes by hand.
/// </summary>
public interface IAutoDiscoverService
{
    /// <summary>
    /// Looks up server settings for an email address, trying the built-in provider table first,
    /// then (unless <c>AutoDiscoverOnline</c> is off) Mozilla's autoconfig database, then the
    /// domain's own Exchange Autodiscover endpoint. Returns the first hit, or null when every tier
    /// is exhausted — callers must surface that null rather than leaving the UI blank.
    /// Never throws for a network or parse failure.
    /// </summary>
    Task<DiscoveredSettings?> DiscoverAsync(string emailAddress, CancellationToken ct);
}
