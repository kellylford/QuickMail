// The two shapes of a sender kept on every list row (#803): the display form the list shows and
// speaks, and the bare address that rules, Find Mail From Contact and search match on. IMAP and POP3
// both build them with these formatters, so the list reads the same whichever backend fetched it.

using MimeKit;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

public class SenderAddressFormattingTests
{
    [Theory]
    [InlineData("Some Company <news@company.com>", "news@company.com")]
    [InlineData("news@company.com",                "news@company.com")]
    [InlineData("\"Ford, Kelly\" <kelly@example.com>", "kelly@example.com")]
    [InlineData("Ann <ann@x.com>, Bob <bob@y.com>",   "ann@x.com, bob@y.com")]
    public void Emails_AreTheBareAddresses(string header, string expected)
        => Assert.Equal(expected, ImapMailService.FormatAddressListEmails(InternetAddressList.Parse(header)));

    [Fact]
    public void Emails_ListAGroupsMembers_NotTheGroup()
        => Assert.Equal("ann@x.com, bob@y.com",
            ImapMailService.FormatAddressListEmails(
                InternetAddressList.Parse("Team: Ann <ann@x.com>, Bob <bob@y.com>;")));

    [Fact]
    public void Emails_AreEmpty_ForNoSender()
    {
        Assert.Equal(string.Empty, ImapMailService.FormatAddressListEmails(null));
        Assert.Equal(string.Empty, ImapMailService.FormatAddressListEmails(new InternetAddressList()));
    }

    [Theory]
    [InlineData("Some Company <news@company.com>", "Some Company")]
    // No name: the address stands in, so a nameless sender is never shown or spoken as blank.
    [InlineData("news@company.com",                "news@company.com")]
    [InlineData("\"Ford, Kelly\" <kelly@example.com>", "Ford, Kelly")]
    public void Display_IsTheNameWhereThereIsOne(string header, string expected)
        => Assert.Equal(expected, ImapMailService.FormatAddressListDisplay(InternetAddressList.Parse(header)));

    [Theory]
    // An IMAP or POP3 row: name and address joined, the shape a Graph row used to hold in From.
    [InlineData("Some Company",     "news@company.com", "Some Company <news@company.com>")]
    // No name: From already is the address, so it is not repeated.
    [InlineData("news@company.com", "news@company.com", "news@company.com")]
    [InlineData("NEWS@company.com", "news@company.com", "NEWS@company.com")]
    // Cached before FromEmail existed: From alone, exactly what matchers read before.
    [InlineData("Some Company",     "",                 "Some Company")]
    [InlineData("Some Company <news@company.com>", "",  "Some Company <news@company.com>")]
    [InlineData("",                 "news@company.com", "news@company.com")]
    public void FromForMatching_JoinsTheNameAndTheAddress(string from, string fromEmail, string expected)
        => Assert.Equal(expected, new MailMessageSummary { From = from, FromEmail = fromEmail }.FromForMatching);
}
