using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Util.Store;

namespace QuickMail.Services;

// ClientId and ClientSecret are defined in GoogleOAuthService.Credentials.cs (gitignored).
// Copy docs/GoogleOAuthService.Credentials.example to
// QuickMail/Services/GoogleOAuthService.Credentials.cs and fill in your values.
public partial class GoogleOAuthService : IGoogleOAuthService
{
    // Gmail IMAP/SMTP access + openid/email so we can confirm the signed-in address from the id_token.
    private static readonly string[] Scopes = ["https://mail.google.com/", "openid", "email"];

    private static string TokenKey(string username)
        => $"QuickMail:GoogleToken:{username.ToLowerInvariant()}";

    private readonly ICredentialService _credentialService;

    // In-memory cache: lowercase username → live UserCredential (holds access token + auto-refresh)
    private readonly Dictionary<string, UserCredential> _cache = [];

    public GoogleOAuthService(ICredentialService credentialService)
    {
        _credentialService = credentialService;
    }

    private static GoogleAuthorizationCodeFlow CreateFlow(IDataStore? dataStore = null)
    {
        return new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId     = ClientId,
                ClientSecret = ClientSecret,
            },
            Scopes    = Scopes,
            DataStore = dataStore ?? new NoOpDataStore(),
        });
    }

    public async Task<string> GetAccessTokenAsync(string username, CancellationToken ct = default)
    {
        var key = username.ToLowerInvariant();

        if (!_cache.TryGetValue(key, out var credential))
        {
            var refreshToken = _credentialService.GetSecret(TokenKey(username));
            if (string.IsNullOrEmpty(refreshToken))
                throw new InvalidOperationException($"No Google credentials stored for {username}. Sign in first.");

            credential = new UserCredential(CreateFlow(new NoOpDataStore()), username, new TokenResponse
            {
                RefreshToken = refreshToken,
            });
            _cache[key] = credential;
        }

        // GetAccessTokenForRequestAsync refreshes automatically when the access token is expired.
        return await credential.GetAccessTokenForRequestAsync(cancellationToken: ct);
    }

    public async Task<OAuthResult> SignInInteractiveAsync(string loginHint, CancellationToken ct = default)
    {
        // NoOpDataStore: we handle persistence ourselves via CredentialService / WCM.
        var flow     = CreateFlow(new NoOpDataStore());
        var receiver = new LocalServerCodeReceiver();
        var app      = new AuthorizationCodeInstalledApp(flow, receiver);

        var userId     = string.IsNullOrEmpty(loginHint) ? "user" : loginHint;
        var credential = await app.AuthorizeAsync(userId, ct);

        var email = ExtractEmailFromIdToken(credential.Token.IdToken) ?? loginHint;
        if (string.IsNullOrEmpty(email))
            throw new InvalidOperationException("Google sign-in completed but the signed-in address could not be determined.");

        var refreshToken = credential.Token.RefreshToken;
        if (!string.IsNullOrEmpty(refreshToken))
            _credentialService.SaveSecret(TokenKey(email), refreshToken);
        else
            LogService.Log("GoogleOAuthService: no refresh token returned — consent may already have been granted.");

        _cache[email.ToLowerInvariant()] = credential;

        LogService.Log($"GoogleOAuthService: interactive sign-in complete for {email}");
        var accessToken = await credential.GetAccessTokenForRequestAsync(cancellationToken: ct);
        return new OAuthResult(accessToken, email);
    }

    public Task SignOutAsync(string username)
    {
        _cache.Remove(username.ToLowerInvariant());
        _credentialService.DeleteSecret(TokenKey(username));
        LogService.Log($"GoogleOAuthService: signed out {username}");
        return Task.CompletedTask;
    }

    // Decodes the JWT payload to extract the email claim without making a network call.
    // Safe here because the token came directly from Google's token endpoint over TLS.
    private static string? ExtractEmailFromIdToken(string? idToken)
    {
        if (string.IsNullOrEmpty(idToken)) return null;

        var parts = idToken.Split('.');
        if (parts.Length < 2) return null;

        var payload = parts[1];
        // Pad base64url to standard base64
        var padded = (payload.Length % 4) switch
        {
            2 => payload + "==",
            3 => payload + "=",
            _ => payload,
        };
        padded = padded.Replace('-', '+').Replace('_', '/');

        try
        {
            var bytes = Convert.FromBase64String(padded);
            var json  = Encoding.UTF8.GetString(bytes);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("email", out var emailProp))
                return emailProp.GetString();
        }
        catch (Exception ex)
        {
            LogService.Log($"GoogleOAuthService: failed to decode id_token payload: {ex.Message}");
        }

        return null;
    }

    // Discards all token-store calls from AuthorizationCodeInstalledApp.
    // We handle persistence ourselves via CredentialService.
    private sealed class NoOpDataStore : IDataStore
    {
        public Task StoreAsync<T>(string key, T value)  => Task.CompletedTask;
        public Task DeleteAsync<T>(string key)           => Task.CompletedTask;
        public Task<T> GetAsync<T>(string key)           => Task.FromResult(default(T)!);
        public Task ClearAsync()                         => Task.CompletedTask;
    }
}
