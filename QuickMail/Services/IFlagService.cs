using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

public interface IFlagService
{
    /// <summary>Raised on the UI thread when flag definitions are created, renamed, or deleted.</summary>
    event EventHandler? FlagDefinitionsChanged;

    Task<List<FlagDefinition>> LoadFlagDefinitionsAsync();
    Task SaveFlagDefinitionsAsync(List<FlagDefinition> flags);

    FlagDefinition GetBuiltInFlag();

    /// <summary>Returns the flag definition the K key applies. Never null — falls back to the built-in flag.</summary>
    Task<FlagDefinition> GetKDefaultFlagAsync();

    /// <summary>Persists the default flag id used by the K key.</summary>
    Task SetKDefaultFlagAsync(Guid flagId);

    /// <summary>
    /// Sets or clears a named flag on a message in the local store and on the server.
    /// Pass null <paramref name="flagId"/> to clear. If <paramref name="flagId"/> is the
    /// built-in flag id the IMAP \Flagged flag is also toggled on the server.
    /// </summary>
    Task SetMessageFlagAsync(
        MailMessageSummary message,
        string? flagId,
        ILocalStoreService localStore,
        IMailService mailService,
        CancellationToken ct = default);

    /// <summary>
    /// Toggles the K-default flag on a message (sets if unflagged, clears if already flagged
    /// with the K-default flag or any flag).
    /// </summary>
    Task ToggleDefaultFlagAsync(
        MailMessageSummary message,
        ILocalStoreService localStore,
        IMailService mailService,
        CancellationToken ct = default);
}
