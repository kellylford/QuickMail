// Deviation from spec: AccountModel uses AccountLabel (computed) for the display name,
// a single Username field (not separate ImapUsername/SmtpUsername), ImapUseSsl/SmtpUseSsl
// (not ImapSsl/SmtpSsl), and AuthType enum (not a UseOAuth bool).

using System;
using System.Collections.Generic;
using QuickMail.Models;

namespace QuickMail.Helpers;

public static class AccountPropertiesBuilder
{
    public static (string Title, IReadOnlyList<PropertySection> Sections)
        Build(AccountModel account, DateTimeOffset? lastSyncedUtc, int cacheCount = 0, DateTimeOffset? oldestCached = null, string? syncWindow = null)
    {
        var identity = new List<PropertyItem>
        {
            new("Display name",   account.AccountLabel),
            new("Email address",  account.Username),
        };

        var imap = new List<PropertyItem>
        {
            new("Server",   account.ImapHost),
            new("Port",     account.ImapPort.ToString()),
            new("Security", account.ImapUseSsl ? "SSL/TLS" : "STARTTLS"),
            new("Username", account.Username),
        };

        var smtp = new List<PropertyItem>
        {
            new("Server",   account.SmtpHost),
            new("Port",     account.SmtpPort.ToString()),
            new("Security", account.SmtpUseSsl ? "SSL/TLS" : "STARTTLS"),
            new("Username", account.Username),
        };

        var auth = new List<PropertyItem>
        {
            new("Authentication",
                account.AuthType switch
                {
                    AuthType.OAuth2Microsoft => "OAuth2 (Microsoft 365)",
                    AuthType.OAuth2Google    => "OAuth2 (Google / Gmail)",
                    _ when account.ImapHost.Equals("imap.mail.me.com", StringComparison.OrdinalIgnoreCase)
                                             => "App-Specific Password (iCloud)",
                    _                        => "Password (Windows Credential Manager)",
                }),
            new("Last synced",
                lastSyncedUtc.HasValue
                    ? lastSyncedUtc.Value.ToLocalTime().ToString("f")
                    : "Not yet synced"),
        };

        var sections = new List<PropertySection>
        {
            new("Identity",        identity),
            new("Incoming (IMAP)", imap),
            new("Outgoing (SMTP)", smtp),
            new("Authentication",  auth),
        };

        // Add Sync section if cache information is available (not in --online mode).
        if (!string.IsNullOrEmpty(syncWindow))
        {
            var sync = new List<PropertyItem>
            {
                new("Messages in cache", cacheCount.ToString("N0")),
                new("Oldest cached", oldestCached.HasValue
                    ? oldestCached.Value.ToLocalTime().ToString("f")
                    : "None"),
                new("Sync window", syncWindow),
            };
            sections.Add(new("Sync", sync));
        }

        return ("Account Properties", sections);
    }
}
