using System.Reflection;
using System.Text;
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
        var message = MimeMessageBuilder.Build(compose, account, UserAgent);

        using var client = new SmtpClient();

        if (account.SmtpAcceptInvalidCert)
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;

        var ssl = account.SmtpUseSsl
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTlsWhenAvailable;

        try
        {
            LogService.Log($"SmtpService: connecting to {account.SmtpHost}:{account.SmtpPort} ssl={ssl}");
            await client.ConnectAsync(account.SmtpHost, account.SmtpPort, ssl, ct);
            LogService.Log($"SmtpService: connected to {account.SmtpHost}:{account.SmtpPort}");

            if (account.AuthType == AuthType.OAuth2Microsoft)
            {
                LogService.Debug($"SmtpService: authenticating via XOAUTH2");
                var token = await _oauth.GetAccessTokenAsync(account, ct);
                await client.AuthenticateAsync(new SaslMechanismOAuth2(account.Username, token), ct);
            }
            else
            {
                await client.AuthenticateAsync(account.Username, password!, ct);
            }
            LogService.Log($"SmtpService: authenticated, sending.");
            await client.SendAsync(message, ct);
            LogService.Log($"SmtpService: send complete");
            await client.DisconnectAsync(true, ct);
        }
        catch (Exception ex)
        {
            LogService.Log($"SmtpService: send failed ({ex.GetType().Name})", ex);
            throw;
        }
    }

    public async Task SendIcsReplyAsync(string icsReplyContent, AccountModel account, string? password,
        string organizerEmail, CancellationToken ct = default)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(account.SenderDisplayName, account.Username));

        if (string.IsNullOrWhiteSpace(organizerEmail))
            throw new ArgumentException("Organizer email is required for ICS reply.", nameof(organizerEmail));

        message.To.Add(MailboxAddress.Parse(organizerEmail));

        message.Subject = "Calendar Response";

        var calendarPart = new TextPart("calendar")
        {
            ContentTransferEncoding = ContentEncoding.Base64,
        };
        calendarPart.ContentType.Parameters.Add("method", "REPLY");
        calendarPart.SetText(Encoding.UTF8, icsReplyContent);

        message.Body = calendarPart;

        using var client = new SmtpClient();

        if (account.SmtpAcceptInvalidCert)
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;

        var ssl = account.SmtpUseSsl
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTlsWhenAvailable;

        try
        {
            LogService.Log($"SmtpService: sending ICS reply to {account.SmtpHost}:{account.SmtpPort}");
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
            LogService.Log($"SmtpService: ICS reply authenticated, sending.");
            await client.SendAsync(message, ct);
            LogService.Log($"SmtpService: ICS reply sent.");
            await client.DisconnectAsync(true, ct);
        }
        catch (Exception ex)
        {
            LogService.Log($"SmtpService: ICS reply send failed ({ex.GetType().Name})", ex);
            throw;
        }
    }
}
