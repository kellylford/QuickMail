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
        Build(AccountModel account, DateTimeOffset? lastSyncedUtc)
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
                account.AuthType == AuthType.OAuth2Microsoft
                    ? "OAuth2 (Microsoft 365)"
                    : "Password (Windows Credential Manager)"),
            new("Last synced",
                lastSyncedUtc.HasValue
                    ? lastSyncedUtc.Value.ToLocalTime().ToString("f")
                    : "Not yet synced"),
        };

        return ("Account Properties", [
            new("Identity",        identity),
            new("Incoming (IMAP)", imap),
            new("Outgoing (SMTP)", smtp),
            new("Authentication",  auth),
        ]);
    }
}
