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
    /// Reduces user input to the bare addr-spec that <see cref="MailboxAddress"/> and the SMTP
    /// envelope can actually carry, or returns false when there is no such address in it.
    ///
    /// Normalizing rather than merely validating, because the two are not the same test here.
    /// <c>MailboxAddress.TryParse</c> accepts a whole mailbox — "Kelly Ford &lt;kelly@example.com&gt;",
    /// an angle-addr, a string with surrounding whitespace — while the
    /// <c>MailboxAddress(name, address)</c> constructor that <see cref="Services.MimeMessageBuilder"/>
    /// calls accepts ONLY the addr-spec and throws a ParseException on every one of those forms.
    /// A validator that said yes to input the builder then threw on would have reproduced the exact
    /// unactionable failure #396 is about, one layer further down. Taking <c>mailbox.Address</c>
    /// strips the display name and the angle brackets and settles it.
    /// </summary>
    public static bool TryNormalize(string? input, out string address)
    {
        address = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;

        if (!MailboxAddress.TryParse(RequireDomain, input.Trim(), out var mailbox)) return false;
        if (string.IsNullOrWhiteSpace(mailbox.Domain)) return false;
        if (string.IsNullOrWhiteSpace(mailbox.Address)) return false;

        address = mailbox.Address;
        return true;
    }

    /// <summary>
    /// True when <paramref name="address"/> contains a single usable mailbox with a domain.
    ///
    /// Deliberately does NOT require a dot in the domain. Recipient checking in the compose window
    /// does, on the reasoning that a typo is likelier than an intranet host; for an account the
    /// user has configured themselves the reverse holds — someone whose mail server really is
    /// <c>user@mailhost</c> must still be able to save the account.
    /// </summary>
    public static bool IsValid(string? address) => TryNormalize(address, out _);
}
