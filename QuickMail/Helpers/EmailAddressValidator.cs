using MimeKit;

namespace QuickMail.Helpers;

/// <summary>
/// Whether a string can serve as an account's own email address — the one that becomes the From
/// header, and therefore the SMTP envelope sender, on everything the account sends.
/// </summary>
internal static class EmailAddressValidator
{
    /// <summary>
    /// MimeKit's default parser accepts a bare local part with no domain at all, because such
    /// addresses are legal in a local mail spool. That leniency is exactly wrong here: "fastfinge"
    /// parses happily and then goes out as MAIL FROM:&lt;fastfinge&gt;, which the server rejects
    /// with an error that names neither the field nor the account (#396). Requiring the domain is
    /// the whole point of this check.
    /// </summary>
    private static readonly ParserOptions RequireDomain = CreateOptions();

    private static ParserOptions CreateOptions()
    {
        var options = ParserOptions.Default.Clone();
        options.AllowAddressesWithoutDomain = false;
        return options;
    }

    /// <summary>
    /// True when <paramref name="address"/> is a single mailbox with a domain.
    ///
    /// Deliberately does NOT require a dot in the domain. Recipient checking in the compose window
    /// does, on the reasoning that a typo is likelier than an intranet host; for an account the
    /// user has configured themselves the reverse holds — someone whose mail server really is
    /// <c>user@mailhost</c> must still be able to save the account.
    /// </summary>
    public static bool IsValid(string? address) =>
        !string.IsNullOrWhiteSpace(address)
        && MailboxAddress.TryParse(RequireDomain, address, out var mailbox)
        && !string.IsNullOrWhiteSpace(mailbox.Domain);
}
