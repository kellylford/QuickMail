using System;

namespace QuickMail.Services;

public interface ICredentialService
{
    void SavePassword(Guid accountId, string password);
    string? GetPassword(Guid accountId);
    void DeletePassword(Guid accountId);

    /// <summary>Stores an arbitrary secret under the given key in Windows Credential Manager.</summary>
    void SaveSecret(string key, string value);
    string? GetSecret(string key);
    void DeleteSecret(string key);
}
