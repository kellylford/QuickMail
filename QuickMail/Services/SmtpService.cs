using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using MailKit.Security;
using QuickMail.Models;

namespace QuickMail.Services;

public class SmtpService : ISendMailService
{
    private readonly IOAuthService _oauth;
    private readonly ISendMailService _graphSmtp;

    public SmtpService(IOAuthService oauth, ISendMailService graphSmtp)
    {
        _oauth = oauth;
        _graphSmtp = graphSmtp;
    }

    public async Task SendAsync(ComposeModel compose, AccountModel account, string? password, CancellationToken ct = default)
    {
        if (account.BackendKind == BackendKind.MicrosoftGraph)
        {
            await _graphSmtp.SendAsync(compose, account, password, ct);
            return;
        }

        var message = MimeMessageBuilder.Build(compose, account, MimeMessageBuilder.AppUserAgent);

        using var client = new SmtpClient();

        if (account.SmtpAcceptInvalidCert)
        {
#pragma warning disable CA5359 // callback intentionally accepts any cert when the user enables SmtpAcceptInvalidCert
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;
#pragma warning restore CA5359
        }

        var ssl = MailSecurity.ForSmtp(account);

        try
        {
            LogService.Log($"SmtpService: connecting to {account.SmtpHost}:{account.SmtpPort} ssl={ssl}");
            await client.ConnectAsync(account.SmtpHost, account.SmtpPort, ssl, ct);
            LogService.Log($"SmtpService: connected to {account.SmtpHost}:{account.SmtpPort}");

            if (account.AuthType is AuthType.OAuth2Microsoft or AuthType.OAuth2Google)
            {
                LogService.Debug($"SmtpService: authenticating via XOAUTH2");
                var token = await _oauth.GetAccessTokenAsync(account, ct);
                await client.AuthenticateAsync(new SaslMechanismOAuth2(account.Username, token), ct);
            }
            else
            {
                await client.AuthenticateAsync(account.AuthUsername, password!, ct);
            }
            LogService.Log($"SmtpService: authenticated, sending.");
            await client.SendAsync(message, ct);
            LogService.Log($"SmtpService: send complete");
        }
        catch (Exception ex)
        {
            LogService.Log($"SmtpService: send failed ({ex.GetType().Name})", ex);
            throw;
        }

        // Outside the try, and swallowing its own failure: the message is ACCEPTED by the server at
        // this point, so a QUIT that throws — a server that drops the connection the instant it
        // takes the message, a network blip — is not a send failure. Reporting one told the user
        // their message had failed while it was already on its way, which is worse than either
        // truth. Disposing the client closes the socket regardless. (#396)
        await DisconnectQuietlyAsync(client, ct);
    }

    /// <summary>
    /// Best-effort QUIT. Never throws: every caller has already completed the work that matters.
    /// </summary>
    private static async Task DisconnectQuietlyAsync(SmtpClient client, CancellationToken ct)
    {
        try
        {
            await client.DisconnectAsync(true, ct);
        }
        catch (Exception ex)
        {
            LogService.Log($"SmtpService: disconnect after send failed, ignoring ({ex.GetType().Name})", ex);
        }
    }

    public async Task VerifyAsync(AccountModel account, string? password, CancellationToken ct = default)
    {
        if (account.BackendKind == BackendKind.MicrosoftGraph)
        {
            await _graphSmtp.VerifyAsync(account, password, ct);
            return;
        }

        using var client = new SmtpClient();

        if (account.SmtpAcceptInvalidCert)
        {
#pragma warning disable CA5359 // callback intentionally accepts any cert when the user enables SmtpAcceptInvalidCert
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;
#pragma warning restore CA5359
        }

        // Same SSL and SASL selection as SendAsync — a verification that connected differently from
        // the real send would prove nothing.
        var ssl = MailSecurity.ForSmtp(account);

        LogService.Log($"SmtpService: verifying {account.SmtpHost}:{account.SmtpPort} ssl={ssl}");
        await client.ConnectAsync(account.SmtpHost, account.SmtpPort, ssl, ct);

        if (account.AuthType is AuthType.OAuth2Microsoft or AuthType.OAuth2Google)
        {
            var token = await _oauth.GetAccessTokenAsync(account, ct);
            await client.AuthenticateAsync(new SaslMechanismOAuth2(account.Username, token), ct);
        }
        else
        {
            await client.AuthenticateAsync(account.AuthUsername, password!, ct);
        }

        // Nothing is sent — authenticating is the whole proof, so a failing QUIT must not turn a
        // successful verification into "Test Connection failed".
        await DisconnectQuietlyAsync(client, ct);
        LogService.Log("SmtpService: verify succeeded");
    }

    public async Task SendIcsReplyAsync(string icsReplyContent, AccountModel account, string? password,
        string organizerEmail, CancellationToken ct = default)
    {
        if (account.BackendKind == BackendKind.MicrosoftGraph)
        {
            await _graphSmtp.SendIcsReplyAsync(icsReplyContent, account, password, organizerEmail, ct);
            return;
        }

        var message = MimeMessageBuilder.BuildIcsReply(account, icsReplyContent, organizerEmail);

        using var client = new SmtpClient();

        if (account.SmtpAcceptInvalidCert)
        {
#pragma warning disable CA5359 // callback intentionally accepts any cert when the user enables SmtpAcceptInvalidCert
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;
#pragma warning restore CA5359
        }

        var ssl = MailSecurity.ForSmtp(account);

        try
        {
            LogService.Log($"SmtpService: sending ICS reply to {account.SmtpHost}:{account.SmtpPort}");
            await client.ConnectAsync(account.SmtpHost, account.SmtpPort, ssl, ct);

            if (account.AuthType is AuthType.OAuth2Microsoft or AuthType.OAuth2Google)
            {
                var token = await _oauth.GetAccessTokenAsync(account, ct);
                await client.AuthenticateAsync(new SaslMechanismOAuth2(account.Username, token), ct);
            }
            else
            {
                await client.AuthenticateAsync(account.AuthUsername, password!, ct);
            }
            LogService.Log($"SmtpService: ICS reply authenticated, sending.");
            await client.SendAsync(message, ct);
            LogService.Log($"SmtpService: ICS reply sent.");
        }
        catch (Exception ex)
        {
            LogService.Log($"SmtpService: ICS reply send failed ({ex.GetType().Name})", ex);
            throw;
        }

        // See SendAsync: the reply has been accepted, so a failing QUIT must not surface as a
        // failure to respond to the invitation.
        await DisconnectQuietlyAsync(client, ct);
    }
}
