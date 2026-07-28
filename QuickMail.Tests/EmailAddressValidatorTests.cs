using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The validator has to be strict enough to catch the input that caused #396 — a login name with no
/// domain, which becomes MAIL FROM:&lt;fastfinge&gt; — without rejecting the many odd-looking
/// addresses that are perfectly real. Both directions are pinned here, because either failure is
/// invisible until someone cannot send.
/// </summary>
public class EmailAddressValidatorTests
{
    [Theory]
    [InlineData("kelly@example.com")]
    [InlineData("kelly+tag@example.com")]           // plus-addressing
    [InlineData("kelly.ford@example.co.uk")]
    [InlineData("kelly@example.technology")]         // long TLD
    [InlineData("kelly@mailhost")]                   // intranet host, no dot
    [InlineData("\"very.unusual\"@example.com")]     // quoted local part
    [InlineData("kelly@münchen.de")]                 // internationalized domain
    [InlineData("kelly@xn--mnchen-3ya.de")]          // punycode
    [InlineData("user@[192.168.1.1]")]               // address literal
    public void RealAddressesAreAccepted(string address)
    {
        Assert.True(EmailAddressValidator.IsValid(address));
    }

    [Theory]
    [InlineData("fastfinge")]                        // the reported input: a login name
    [InlineData("DOMAIN\\user")]
    [InlineData("kelly@")]
    [InlineData("@example.com")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void InputWithNoUsableAddressIsRejected(string? address)
    {
        Assert.False(EmailAddressValidator.IsValid(address));
    }

    /// <summary>
    /// The distinction that makes this a normalizer rather than a predicate. MimeKit parses all of
    /// these as a mailbox, but MimeMessageBuilder's MailboxAddress(name, address) constructor throws
    /// on every one — so accepting them unchanged would move the failure from a refused save to a
    /// rejected send, which is the same unactionable error #396 is about.
    /// </summary>
    [Theory]
    [InlineData("Kelly Ford <kelly@example.com>", "kelly@example.com")]
    [InlineData("<kelly@example.com>", "kelly@example.com")]
    [InlineData("  kelly@example.com  ", "kelly@example.com")]
    [InlineData("kelly@example.com", "kelly@example.com")]
    public void AMailboxIsReducedToItsBareAddress(string input, string expected)
    {
        Assert.True(EmailAddressValidator.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    /// <summary>Whatever comes out must be something MimeKit will build a From header from.</summary>
    [Theory]
    [InlineData("Kelly Ford <kelly@example.com>")]
    [InlineData("  kelly@example.com  ")]
    [InlineData("kelly+tag@example.com")]
    [InlineData("kelly@mailhost")]
    public void TheNormalizedFormAlwaysConstructsAMailboxAddress(string input)
    {
        Assert.True(EmailAddressValidator.TryNormalize(input, out var normalized));

        var mailbox = new MimeKit.MailboxAddress("Display Name", normalized);   // must not throw

        Assert.Equal(normalized, mailbox.Address);
    }

    [Fact]
    public void RejectedInputYieldsAnEmptyNormalizedValue()
    {
        Assert.False(EmailAddressValidator.TryNormalize("fastfinge", out var normalized));
        Assert.Equal(string.Empty, normalized);
    }
}
