using System;
using System.Net;
using AdysTech.CredentialManager;

namespace QuickMail.Services;

public class CredentialService : ICredentialService
{
    private static string Key(Guid accountId) => $"QuickMail:{accountId}";

    public void SavePassword(Guid accountId, string password)
    {
        var credential = new NetworkCredential(accountId.ToString(), password);
        CredentialManager.SaveCredentials(Key(accountId), credential);
    }

    public string? GetPassword(Guid accountId)
    {
        var credential = CredentialManager.GetCredentials(Key(accountId));
        return credential?.SecurePassword is { } sp && sp.Length > 0
            ? new NetworkCredential(string.Empty, sp).Password
            : credential?.Password;
    }

    public void DeletePassword(Guid accountId)
    {
        try { CredentialManager.RemoveCredentials(Key(accountId)); }
        catch { /* already gone */ }
    }

    public void SaveSecret(string key, string value)
    {
        var credential = new NetworkCredential(key, value);
        CredentialManager.SaveCredentials(key, credential);
    }

    public string? GetSecret(string key)
    {
        var credential = CredentialManager.GetCredentials(key);
        return credential?.SecurePassword is { } sp && sp.Length > 0
            ? new NetworkCredential(string.Empty, sp).Password
            : credential?.Password;
    }

    public void DeleteSecret(string key)
    {
        try { CredentialManager.RemoveCredentials(key); }
        catch { /* already gone */ }
    }
}
