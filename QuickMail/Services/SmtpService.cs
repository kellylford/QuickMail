using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using QuickMail.Models;

namespace QuickMail.Services;

public class SmtpService : ISmtpService
{
    private readonly IOAuthService _oauth;
    private static readonly string UserAgent =
        "QuickMail/" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0");

    public SmtpService(IOAuthService oauth) => _oauth = oauth;

    public async Task SendAsync(ComposeModel compose, AccountModel account, string? password, CancellationToken ct = default)
    {
        var message = new MimeMessage();

        message.Headers.Add(HeaderId.UserAgent, UserAgent);
        message.From.Add(new MailboxAddress(account.DisplayName, account.Username));
        AddAddresses(message.To, compose.To);
        AddAddresses(message.Cc, compose.Cc);
        AddAddresses(message.Bcc, compose.Bcc);
        message.Subject = compose.Subject;

        if (!string.IsNullOrEmpty(compose.InReplyToMessageId))
            message.InReplyTo = compose.InReplyToMessageId;

        var body = new TextPart("plain") { Text = compose.Body };
        message.Body = body;

        using var client = new SmtpClient();

        if (account.SmtpAcceptInvalidCert)
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;

        var ssl = account.SmtpUseSsl
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTlsWhenAvailable;

        await client.ConnectAsync(account.SmtpHost, account.SmtpPort, ssl, ct);

        if (account.AuthType == AuthType.OAuth2Microsoft)
        {
            var token = await _oauth.GetAccessTokenAsync(account, ct);
            await client.AuthenticateAsync(new SaslMechanismOAuth2(account.Username, token), ct);
        }
        else
        {
            await client.AuthenticateAsync(account.Username, password!, ct);
        }

        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }

    private static void AddAddresses(InternetAddressList list, string addressString)
    {
        if (string.IsNullOrWhiteSpace(addressString)) return;
        foreach (var part in addressString.Split(',', ';'))
        {
            var trimmed = part.Trim();
            if (!string.IsNullOrEmpty(trimmed) && MailboxAddress.TryParse(trimmed, out var addr))
                list.Add(addr);
        }
    }
}
