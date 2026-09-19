using System;
using System.Security.Cryptography;

namespace QuickMail.Helpers;

/// <summary>
/// The <c>quickmail:</c> links QuickMail itself puts into a message document — the invitation card's
/// Accept, Tentative and Decline buttons. Each carries a random token made fresh for every run of the
/// app, and a <c>quickmail:</c> URL without it does nothing.
/// <para>Why: the scheme is QuickMail's, but the document around the card is the sender's. Without the
/// token a sender could write <c>quickmail:ics-accept</c> into the message — as a link labelled
/// "Read more", or as a refresh that fires on its own — and accept their own invitation from your
/// calendar the moment you read it. Found by the #728 security review, 2026-09-18. The token never
/// leaves the process: the card is not part of any saved copy of a message.</para>
/// </summary>
public static class QuickMailLinks
{
    /// <summary>This run's token: 128 random bits, as hex.</summary>
    internal static readonly string Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>The link for one of QuickMail's own actions, e.g. <c>ics-accept</c>.</summary>
    public static string Build(string action) => $"quickmail:{action}?t={Token}";

    /// <summary>
    /// The action a <c>quickmail:</c> URL names, when — and only when — it carries this run's token.
    /// Anything else, including a well-formed link from a previous run, yields false.
    /// </summary>
    public static bool TryParse(string? uri, out string action)
    {
        action = string.Empty;
        const string scheme = "quickmail:";
        if (uri is null || !uri.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;

        var rest = uri[scheme.Length..];
        var q = rest.IndexOf('?');
        if (q <= 0) return false;
        var query = rest[(q + 1)..];
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(query),
                System.Text.Encoding.ASCII.GetBytes("t=" + Token)))
            return false;

        action = rest[..q].ToLowerInvariant();
        return true;
    }
}
