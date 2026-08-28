using System;

namespace QuickMail.Services;

/// <summary>
/// Identity for a message that exists only on this computer — a POP3 message the server has already
/// dropped, or a draft that has not been uploaded yet (#637).
/// <para>The prefix is load-bearing rather than cosmetic: sync's deletion reconcile treats "absent
/// from the server listing" as "deleted on the server", which is the right reading for every id the
/// server has ever issued and exactly the wrong one for an id it has never seen. Anything that
/// compares local ids against a server listing must exclude these.</para>
/// </summary>
public static class LocalMessageId
{
    /// <summary>Prefix marking an id minted here. Was private to the POP3 backend until drafts needed it too.</summary>
    public const string Prefix = "local-";

    public static string New() => Prefix + Guid.NewGuid().ToString("N");

    public static bool IsLocal(string? messageId) =>
        messageId != null && messageId.StartsWith(Prefix, StringComparison.Ordinal);
}
