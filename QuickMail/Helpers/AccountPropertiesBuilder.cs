// Deviation from spec: AccountModel uses AccountLabel (computed) for the display name, one
// AuthUsername shared by both legs (not separate ImapUsername/SmtpUsername), ImapUseSsl/SmtpUseSsl
// (not ImapSsl/SmtpSsl), and AuthType enum (not a UseOAuth bool).

using System;
using System.Collections.Generic;
using QuickMail.Models;
using QuickMail.Services;

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

        // Graph has no host, port or security of its own, and describes its incoming and outgoing
        // legs identically — so the rows are built once rather than written out on both sides.
        List<PropertyItem> GraphLeg() =>
        [
            new("Connection", "Microsoft Graph"),
            new("Server",     "None — Graph uses the Microsoft 365 API"),
            new("Username",   account.Username),
        ];

        // Incoming: whichever protocol this account actually receives over. Reading the IMAP fields
        // regardless was wrong for every account that does not speak IMAP — a POP3 or Graph account
        // showed a blank server on port 993, which is not a description of anything (#128).
        var (incomingHeader, incoming) = account.BackendKind switch
        {
            BackendKind.Pop3Smtp => ("Incoming (POP3)", new List<PropertyItem>
            {
                new("Server",   account.Pop3Host),
                new("Port",     account.Pop3Port.ToString()),
                new("Security", account.Pop3UseSsl ? "SSL/TLS" : "STARTTLS"),
                new("Username", account.AuthUsername),
                // The setting with consequences: cleared, this computer holds the only copy.
                new("Mail on server", account.Pop3LeaveMailOnServer
                    ? "Kept after downloading"
                    : "Removed once downloaded"),
            }),

            BackendKind.MicrosoftGraph => ("Incoming (Microsoft 365)", GraphLeg()),

            _ => ("Incoming (IMAP)", new List<PropertyItem>
            {
                new("Server",   account.ImapHost),
                new("Port",     account.ImapPort.ToString()),
                new("Security", account.ImapUseSsl ? "SSL/TLS" : "STARTTLS"),
                // AuthUsername, not Username: these two rows describe how the account CONNECTS, so on
                // an account with a separate login name they must show the name actually sent (#396).
                new("Username", account.AuthUsername),
            }),
        };

        // Outgoing: POP3 accounts send over SMTP exactly like IMAP ones, so only Graph differs.
        var (outgoingHeader, outgoing) = account.BackendKind == BackendKind.MicrosoftGraph
            ? ("Outgoing (Microsoft 365)", GraphLeg())
            : ("Outgoing (SMTP)", new List<PropertyItem>
            {
                new("Server",   account.SmtpHost),
                new("Port",     account.SmtpPort.ToString()),
                new("Security", account.SmtpUseSsl ? "SSL/TLS" : "STARTTLS"),
                new("Username", account.AuthUsername),
            });

        var auth = new List<PropertyItem>
        {
            new("Authentication",
                account.AuthType switch
                {
                    AuthType.OAuth2Microsoft => "OAuth2 (Microsoft 365)",
                    AuthType.OAuth2Google    => "OAuth2 (Google / Gmail)",
                    _ when ProviderCatalog.IsICloud(account)
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
            new("Identity",       identity),
            new(incomingHeader,   incoming),
            new(outgoingHeader,   outgoing),
            new("Authentication", auth),
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
