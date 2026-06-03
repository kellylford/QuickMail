using System;
using System.Collections.Generic;
using System.Linq;
using QuickMail.Helpers;
using QuickMail.Models;
using Xunit;

namespace QuickMail.Tests;

// ── MessagePropertiesBuilder ────────────────────────────────────────────────

public class MessagePropertiesBuilderTests
{
    private static MailMessageSummary MakeSummary(string from = "alice@example.com", string to = "bob@example.com",
        string subject = "Hello", bool isRead = false) => new()
    {
        UniqueId  = 1,
        AccountId = Guid.NewGuid(),
        FolderName = "INBOX",
        From    = from,
        To      = to,
        Subject = subject,
        Date    = new DateTimeOffset(2026, 6, 2, 10, 34, 0, TimeSpan.Zero),
        IsRead  = isRead,
    };

    [Fact]
    public void Build_PopulatesFromAndSubject()
    {
        var s = MakeSummary(from: "alice@example.com", subject: "Test subject");
        var (title, sections) = MessagePropertiesBuilder.Build(s, null, "Work");

        Assert.Equal("Message Properties", title);
        var headers = sections.First(x => x.Header == "Headers");
        Assert.Contains(headers.Items, i => i.Label == "From"    && i.Value.Contains("alice"));
        Assert.Contains(headers.Items, i => i.Label == "Subject" && i.Value == "Test subject");
    }

    [Fact]
    public void Build_FormatsDateInLocalTime()
    {
        var s = MakeSummary();
        var (_, sections) = MessagePropertiesBuilder.Build(s, null, "Work");

        var headers = sections.First(x => x.Header == "Headers");
        var dateRow = headers.Items.First(i => i.Label == "Date");
        // The formatted date should be non-empty and not a raw DateTimeOffset.
        Assert.False(string.IsNullOrEmpty(dateRow.Value));
        Assert.DoesNotContain("+00:00", dateRow.Value);
    }

    [Fact]
    public void Build_WithNullDetail_OmitsContentSection()
    {
        var s = MakeSummary();
        var (_, sections) = MessagePropertiesBuilder.Build(s, null, "Work");

        Assert.DoesNotContain(sections, sec => sec.Header == "Content");
    }

    [Fact]
    public void Build_WithNullDetail_ShowsNotLoadedForDetailFields()
    {
        var s = MakeSummary();
        var (_, sections) = MessagePropertiesBuilder.Build(s, null, "Work");

        var headers = sections.First(x => x.Header == "Headers");
        Assert.Contains(headers.Items, i => i.Label == "Cc"         && i.Value == "(not loaded)");
        Assert.Contains(headers.Items, i => i.Label == "Reply-To"   && i.Value == "(not loaded)");
        Assert.Contains(headers.Items, i => i.Label == "Message-ID" && i.Value == "(not loaded)");
    }

    [Fact]
    public void Build_WithAttachments_IncludesCount()
    {
        var s = MakeSummary();
        var d = new MailMessageDetail
        {
            UniqueId    = s.UniqueId,
            AccountId   = s.AccountId,
            FolderName  = s.FolderName,
            MessageId   = "<abc@example.com>",
            Attachments = [new() { FileName = "file.pdf", FileSize = 1024 }],
            HtmlBody    = "<p>Hello</p>",
        };

        var (_, sections) = MessagePropertiesBuilder.Build(s, d, "Work");

        var content = sections.First(x => x.Header == "Content");
        Assert.Contains(content.Items, i => i.Label == "Attachments" && i.Value.Contains("1"));
    }

    [Fact]
    public void Build_WithHtmlAndPlainText_ShowsBothFormat()
    {
        var s = MakeSummary();
        var d = new MailMessageDetail
        {
            UniqueId   = s.UniqueId,
            AccountId  = s.AccountId,
            FolderName = s.FolderName,
            HtmlBody   = "<p>Hello</p>",
            PlainTextBody = "Hello",
        };

        var (_, sections) = MessagePropertiesBuilder.Build(s, d, "Work");

        var content = sections.First(x => x.Header == "Content");
        Assert.Contains(content.Items, i => i.Label == "Format"
            && i.Value == "HTML with plain-text alternative");
    }

    [Fact]
    public void Build_UnreadMessage_ShowsUnreadInStatus()
    {
        var s = MakeSummary(isRead: false);
        var (_, sections) = MessagePropertiesBuilder.Build(s, null, "Work");

        var storage = sections.First(x => x.Header == "Storage");
        Assert.Contains(storage.Items, i => i.Label == "Status" && i.Value.Contains("Unread"));
    }

    [Fact]
    public void Build_ReadMessage_ShowsReadInStatus()
    {
        var s = MakeSummary(isRead: true);
        var (_, sections) = MessagePropertiesBuilder.Build(s, null, "Work");

        var storage = sections.First(x => x.Header == "Storage");
        Assert.Contains(storage.Items, i => i.Label == "Status" && i.Value == "Read");
    }
}

// ── FolderPropertiesBuilder ─────────────────────────────────────────────────

public class FolderPropertiesBuilderTests
{
    [Fact]
    public void Build_VirtualFolder_ShowsVirtualFolderType()
    {
        var folder = new MailFolderModel
        {
            FullName    = "\u0000AllMail",
            DisplayName = "All Mail",
            AccountId   = Guid.Empty,
        };

        var (_, sections) = FolderPropertiesBuilder.Build(folder, "All accounts");

        var items = sections[0].Items;
        Assert.Contains(items, i => i.Label == "Type" && i.Value == "Virtual folder");
    }

    [Fact]
    public void Build_RealFolder_ShowsImapFolderType()
    {
        var folder = new MailFolderModel
        {
            FullName    = "INBOX",
            DisplayName = "Inbox",
            Kind        = SpecialFolderKind.Inbox,
        };

        var (_, sections) = FolderPropertiesBuilder.Build(folder, "Work");

        var items = sections[0].Items;
        Assert.Contains(items, i => i.Label == "Type" && i.Value == "IMAP folder");
    }

    [Fact]
    public void Build_ExcludedFolder_ShowsYes()
    {
        var folder = new MailFolderModel
        {
            FullName          = "Trash",
            DisplayName       = "Trash",
            ExcludeFromAllMail = true,
        };

        var (_, sections) = FolderPropertiesBuilder.Build(folder, "Work");

        Assert.Contains(sections[0].Items, i =>
            i.Label == "Excluded from All Mail" && i.Value == "Yes");
    }

    [Fact]
    public void Build_IncludesMessageCounts()
    {
        var folder = new MailFolderModel
        {
            FullName     = "INBOX",
            DisplayName  = "Inbox",
            MessageCount = 1247,
            UnreadCount  = 12,
        };

        var (_, sections) = FolderPropertiesBuilder.Build(folder, "Work");

        var items = sections[0].Items;
        Assert.Contains(items, i => i.Label == "Total messages"  && i.Value.Contains("1"));
        Assert.Contains(items, i => i.Label == "Unread messages" && i.Value == "12");
    }
}

// ── AccountPropertiesBuilder ────────────────────────────────────────────────

public class AccountPropertiesBuilderTests
{
    private static AccountModel MakeAccount(AuthType authType = AuthType.Password) => new()
    {
        AccountName = "Work",
        Username    = "alice@work.com",
        ImapHost    = "imap.work.com",
        ImapPort    = 993,
        ImapUseSsl  = true,
        SmtpHost    = "smtp.work.com",
        SmtpPort    = 587,
        SmtpUseSsl  = false,
        AuthType    = authType,
    };

    [Fact]
    public void Build_OAuthAccount_ShowsOAuthNotPassword()
    {
        var acct = MakeAccount(AuthType.OAuth2Microsoft);
        var (_, sections) = AccountPropertiesBuilder.Build(acct, null);

        var auth = sections.First(s => s.Header == "Authentication");
        Assert.Contains(auth.Items, i =>
            i.Label == "Authentication" && i.Value.Contains("OAuth2"));
    }

    [Fact]
    public void Build_PasswordAccount_ShowsCredentialManager()
    {
        var acct = MakeAccount(AuthType.Password);
        var (_, sections) = AccountPropertiesBuilder.Build(acct, null);

        var auth = sections.First(s => s.Header == "Authentication");
        Assert.Contains(auth.Items, i =>
            i.Label == "Authentication" && i.Value.Contains("Credential Manager"));
    }

    [Fact]
    public void Build_NeverExposeCredential()
    {
        var acct = MakeAccount();
        var (_, sections) = AccountPropertiesBuilder.Build(acct, null);

        // No value in any section should contain "password", a token, or a credential hint
        // beyond the allowed "Windows Credential Manager" label.
        foreach (var section in sections)
        {
            foreach (var item in section.Items)
            {
                if (item.Label == "Authentication") continue; // allowed to say "Credential Manager"
                Assert.DoesNotContain("password", item.Value, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("token",    item.Value, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("secret",   item.Value, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Build_NullLastSynced_ShowsNotYetSynced()
    {
        var acct = MakeAccount();
        var (_, sections) = AccountPropertiesBuilder.Build(acct, null);

        var auth = sections.First(s => s.Header == "Authentication");
        Assert.Contains(auth.Items, i =>
            i.Label == "Last synced" && i.Value == "Not yet synced");
    }

    [Fact]
    public void Build_WithLastSynced_ShowsFormattedDate()
    {
        var acct = MakeAccount();
        var synced = new DateTimeOffset(2026, 6, 2, 10, 30, 0, TimeSpan.Zero);
        var (_, sections) = AccountPropertiesBuilder.Build(acct, synced);

        var auth = sections.First(s => s.Header == "Authentication");
        var lastSyncedRow = auth.Items.First(i => i.Label == "Last synced");
        Assert.NotEqual("Not yet synced", lastSyncedRow.Value);
        Assert.False(string.IsNullOrEmpty(lastSyncedRow.Value));
    }
}

// ── ContactPropertiesBuilder ────────────────────────────────────────────────

public class ContactPropertiesBuilderTests
{
    [Fact]
    public void Build_NoGroups_ShowsNotMemberOfAnyGroups()
    {
        var contact = new ContactModel { DisplayName = "Alice", EmailAddress = "alice@example.com" };
        var (_, sections) = ContactPropertiesBuilder.Build(contact, []);

        Assert.Contains(sections[0].Items, i =>
            i.Label == "Groups" && i.Value == "Not a member of any groups");
    }

    [Fact]
    public void Build_MultipleGroups_JoinsWithComma()
    {
        var contact = new ContactModel { DisplayName = "Alice", EmailAddress = "alice@example.com" };
        var (_, sections) = ContactPropertiesBuilder.Build(contact, ["Team A", "Team B"]);

        Assert.Contains(sections[0].Items, i =>
            i.Label == "Groups" && i.Value.Contains("Team A") && i.Value.Contains("Team B"));
    }

    [Fact]
    public void Build_NeverUsed_ShowsNever()
    {
        var contact = new ContactModel { EmailAddress = "alice@example.com", LastUsedTicks = 0 };
        var (_, sections) = ContactPropertiesBuilder.Build(contact, []);

        Assert.Contains(sections[0].Items, i =>
            i.Label == "Last used" && i.Value == "Never");
    }
}

// ── GroupPropertiesBuilder ──────────────────────────────────────────────────

public class GroupPropertiesBuilderTests
{
    [Fact]
    public void Build_EmptyGroup_ShowsZeroMembers()
    {
        var group = new GroupModel { Name = "Empty", MemberContactIds = [] };
        var (_, sections) = GroupPropertiesBuilder.Build(group, []);

        Assert.Contains(sections[0].Items, i =>
            i.Label == "Members" && i.Value == "0 of 0");
    }

    [Fact]
    public void Build_WithMembers_CreatesSubListSection()
    {
        var group = new GroupModel
        {
            Name             = "Team",
            MemberContactIds = [1, 2],
            ResolvedMemberCount = 2,
        };
        var members = new List<ContactModel>
        {
            new() { DisplayName = "Alice", EmailAddress = "alice@example.com" },
            new() { DisplayName = "Bob",   EmailAddress = "bob@example.com" },
        };

        var (_, sections) = GroupPropertiesBuilder.Build(group, members);

        var membersSection = sections.First(s => s.Header == "Members");
        Assert.Equal(2, membersSection.Items.Count);
        Assert.Contains(membersSection.Items, i => i.Label == "Alice");
        Assert.Contains(membersSection.Items, i => i.Label == "Bob");
    }

    [Fact]
    public void Build_MissingContacts_ShowsMissingCount()
    {
        var group = new GroupModel
        {
            Name                = "Team",
            MemberContactIds    = [1, 2, 3],  // 3 IDs
            ResolvedMemberCount = 2,           // only 2 resolve
        };

        var (_, sections) = GroupPropertiesBuilder.Build(group, [
            new() { DisplayName = "Alice", EmailAddress = "alice@example.com" },
            new() { DisplayName = "Bob",   EmailAddress = "bob@example.com" },
        ]);

        Assert.Contains(sections[0].Items, i =>
            i.Label == "Missing contacts" && i.Value == "1");
    }
}

// ── AttachmentPropertiesBuilder ─────────────────────────────────────────────

public class AttachmentPropertiesBuilderTests
{
    [Fact]
    public void Build_SetsFileNameAndMimeType()
    {
        var att = new AttachmentModel { FileName = "report.pdf", ContentType = "application/pdf", FileSize = 245_760 };
        var (title, sections) = AttachmentPropertiesBuilder.Build(att);

        Assert.Equal("Attachment Properties", title);
        var items = sections[0].Items;
        Assert.Contains(items, i => i.Label == "File name" && i.Value == "report.pdf");
        Assert.Contains(items, i => i.Label == "MIME type"  && i.Value == "application/pdf");
    }

    [Fact]
    public void Build_EmptyFileName_ShowsUnnamed()
    {
        var att = new AttachmentModel { FileName = "", ContentType = "application/octet-stream" };
        var (_, sections) = AttachmentPropertiesBuilder.Build(att);

        Assert.Contains(sections[0].Items, i => i.Label == "File name" && i.Value == "(unnamed)");
    }

    [Fact]
    public void Build_FormatsSizeAsKb()
    {
        var att = new AttachmentModel { FileName = "file.pdf", FileSize = 2048 };
        var (_, sections) = AttachmentPropertiesBuilder.Build(att);

        Assert.Contains(sections[0].Items, i => i.Label == "Size" && i.Value.Contains("KB"));
    }

    [Fact]
    public void Build_ZeroSize_ShowsUnknown()
    {
        var att = new AttachmentModel { FileName = "file.pdf", FileSize = 0 };
        var (_, sections) = AttachmentPropertiesBuilder.Build(att);

        Assert.Contains(sections[0].Items, i => i.Label == "Size" && i.Value == "Unknown");
    }
}
