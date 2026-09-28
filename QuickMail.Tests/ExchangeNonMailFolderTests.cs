using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Issue #613: an Exchange Online or Outlook.com mailbox reached over IMAP lists its calendar,
/// contacts, tasks and other non-mail folders as ordinary folders, which then fail to open. The
/// IMAP folder list drops them — but only for Microsoft servers, and only at the top level.
/// </summary>
public class ExchangeNonMailFolderTests
{
    [Theory]
    [InlineData("Calendar")]
    [InlineData("Contacts")]
    [InlineData("Tasks")]
    [InlineData("Notes")]
    [InlineData("Journal")]
    [InlineData("Conversation History")]
    [InlineData("Sync Issues")]
    [InlineData("Outbox")]
    [InlineData("RSS Feeds")]
    public void WellKnownTopLevelName_IsNonMail(string name)
        => Assert.True(ImapMailService.IsExchangeNonMailFolder(name, '/'));

    [Theory]
    [InlineData("Calendar/Birthdays")]
    [InlineData("Contacts/Recipient Cache")]
    [InlineData("Sync Issues/Conflicts")]
    [InlineData("Sync Issues/Local Failures")]
    [InlineData("Sync Issues/Server Failures")]
    public void ChildOfNonMailFolder_IsNonMail(string fullName)
        => Assert.True(ImapMailService.IsExchangeNonMailFolder(fullName, '/'));

    [Fact]
    public void MatchIgnoresCase()
        => Assert.True(ImapMailService.IsExchangeNonMailFolder("CALENDAR", '/'));

    [Theory]
    [InlineData("Inbox/Notes")]        // a user's own folder under Inbox is mail
    [InlineData("Projects/Calendar")]
    [InlineData("Archive")]
    [InlineData("Sent Items")]
    [InlineData("Deleted Items")]
    [InlineData("Junk Email")]
    [InlineData("Calendar stuff")]     // only the exact name, not a prefix
    [InlineData("")]
    public void MailFolder_IsKept(string fullName)
        => Assert.False(ImapMailService.IsExchangeNonMailFolder(fullName, '/'));

    [Fact]
    public void NoSeparator_MatchesWholeName()
    {
        Assert.True(ImapMailService.IsExchangeNonMailFolder("Calendar", '\0'));
        Assert.False(ImapMailService.IsExchangeNonMailFolder("Inbox/Notes", '\0'));
    }

    [Theory]
    [InlineData("outlook.office365.com")]
    [InlineData("OUTLOOK.OFFICE365.COM")]
    [InlineData("outlook.office365.com.")]
    [InlineData("outlook.office.com")]
    [InlineData("imap-mail.outlook.com")]
    [InlineData("imap.outlook.com")]
    public void MicrosoftHost_IsMicrosoft(string host)
        => Assert.True(ImapMailService.IsMicrosoftImapAccount(new AccountModel { ImapHost = host }));

    [Fact]
    public void MicrosoftOAuth_IsMicrosoftWhateverTheHost()
        => Assert.True(ImapMailService.IsMicrosoftImapAccount(
            new AccountModel { ImapHost = "mail.example.com", AuthType = AuthType.OAuth2Microsoft }));

    [Theory]
    [InlineData("imap.gmail.com")]
    [InlineData("imap.fastmail.com")]
    [InlineData("mail.example.com")]
    [InlineData("outlook.office365.com.evil.example")]
    [InlineData("")]
    public void OtherServer_IsNotMicrosoft(string host)
        => Assert.False(ImapMailService.IsMicrosoftImapAccount(new AccountModel { ImapHost = host }));
}
