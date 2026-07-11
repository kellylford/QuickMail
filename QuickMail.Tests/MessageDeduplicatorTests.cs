using System;
using System.Collections.Generic;
using System.Linq;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for <see cref="MessageDeduplicator"/> — the collapse of one physical message's per-folder
/// copies in aggregate views (issue #220, Gmail duplicates). The correctness-critical invariants are
/// the empty-identity and cross-account rules, and representative selection by folder priority.
/// </summary>
public class MessageDeduplicatorTests
{
    private static readonly Guid AccountA = Guid.NewGuid();
    private static readonly Guid AccountB = Guid.NewGuid();

    private static MailMessageSummary Msg(
        string internetId, string folder, SpecialFolderKind kind = SpecialFolderKind.None,
        Guid? account = null, string uid = "1", int dayOffset = 0)
        => new()
        {
            MessageId         = uid,
            AccountId         = account ?? AccountA,
            FolderName        = folder,
            InternetMessageId = internetId,
            Subject           = "Subject",
            Date              = DateTimeOffset.UtcNow.AddDays(dayOffset),
            // Kind is carried out-of-band via the resolver below, not on the summary.
        };

    // A resolver that maps folder name -> kind for the test fixtures.
    private static Func<MailMessageSummary, SpecialFolderKind> KindByFolder(
        IDictionary<string, SpecialFolderKind> map)
        => m => map.TryGetValue(m.FolderName, out var k) ? k : SpecialFolderKind.None;

    private static readonly Dictionary<string, SpecialFolderKind> GmailFolders = new()
    {
        ["INBOX"]              = SpecialFolderKind.Inbox,
        ["[Gmail]/All Mail"]   = SpecialFolderKind.AllMail,
        ["[Gmail]/Important"]  = SpecialFolderKind.Important,
        ["[Gmail]/Starred"]    = SpecialFolderKind.Starred,
        ["Work"]               = SpecialFolderKind.None,
    };

    [Fact]
    public void CollapsesGmailCopiesOfOneMessageToOne()
    {
        var input = new[]
        {
            Msg("<abc@mail>", "INBOX",             uid: "10"),
            Msg("<abc@mail>", "[Gmail]/All Mail",  uid: "20"),
            Msg("<abc@mail>", "[Gmail]/Important", uid: "30"),
            Msg("<abc@mail>", "Work",              uid: "40"),
        };

        var result = MessageDeduplicator.CollapseForAggregate(input, KindByFolder(GmailFolders));

        Assert.Single(result);
    }

    [Fact]
    public void RepresentativeIsInboxCopy()
    {
        var input = new[]
        {
            Msg("<abc@mail>", "[Gmail]/All Mail",  uid: "20"),
            Msg("<abc@mail>", "Work",              uid: "40"),
            Msg("<abc@mail>", "INBOX",             uid: "10"),
        };

        var result = MessageDeduplicator.CollapseForAggregate(input, KindByFolder(GmailFolders));

        Assert.Equal("INBOX", Assert.Single(result).FolderName);
    }

    [Fact]
    public void PrefersUserLabelOverGmailVirtualFolders()
    {
        // No INBOX copy — a real user label should still beat All Mail / Important / Starred.
        var input = new[]
        {
            Msg("<abc@mail>", "[Gmail]/Important", uid: "30"),
            Msg("<abc@mail>", "[Gmail]/All Mail",  uid: "20"),
            Msg("<abc@mail>", "Work",              uid: "40"),
        };

        var result = MessageDeduplicator.CollapseForAggregate(input, KindByFolder(GmailFolders));

        Assert.Equal("Work", Assert.Single(result).FolderName);
    }

    [Fact]
    public void EmptyMessageIdRowsAreNeverMerged()
    {
        // Two DISTINCT messages that both lack a Message-ID must stay two rows.
        var input = new[]
        {
            Msg("", "INBOX", uid: "10"),
            Msg("", "INBOX", uid: "11"),
            Msg("", "Work",  uid: "12"),
        };

        var result = MessageDeduplicator.CollapseForAggregate(input, KindByFolder(GmailFolders));

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void SameMessageIdOnDifferentAccountsDoesNotCollapse()
    {
        var input = new[]
        {
            Msg("<abc@mail>", "INBOX", account: AccountA, uid: "10"),
            Msg("<abc@mail>", "INBOX", account: AccountB, uid: "10"),
        };

        var result = MessageDeduplicator.CollapseForAggregate(input, KindByFolder(GmailFolders));

        Assert.Equal(2, result.Count);
    }

    [Theory]
    [InlineData("<ABC@Mail>")]
    [InlineData("abc@mail")]
    [InlineData("  <abc@mail>  ")]
    public void NormalizationCollapsesBracketCaseAndWhitespaceVariants(string variant)
    {
        var input = new[]
        {
            Msg("<abc@mail>", "INBOX",            uid: "10"),
            Msg(variant,      "[Gmail]/All Mail", uid: "20"),
        };

        var result = MessageDeduplicator.CollapseForAggregate(input, KindByFolder(GmailFolders));

        Assert.Single(result);
    }

    [Fact]
    public void IsIdempotent()
    {
        var input = new[]
        {
            Msg("<a@m>", "INBOX",            uid: "1"),
            Msg("<a@m>", "[Gmail]/All Mail", uid: "2"),
            Msg("<b@m>", "INBOX",            uid: "3"),
        };

        var once  = MessageDeduplicator.CollapseForAggregate(input, KindByFolder(GmailFolders));
        var twice = MessageDeduplicator.CollapseForAggregate(once,  KindByFolder(GmailFolders));

        Assert.Equal(once.Count, twice.Count);
        Assert.Equal(once.Select(m => m.FolderName), twice.Select(m => m.FolderName));
    }

    [Fact]
    public void PreservesInputOrderOfRepresentatives()
    {
        var input = new[]
        {
            Msg("<newer@m>", "INBOX", uid: "1", dayOffset: 0),
            Msg("<older@m>", "INBOX", uid: "2", dayOffset: -5),
        };

        var result = MessageDeduplicator.CollapseForAggregate(input, KindByFolder(GmailFolders));

        Assert.Equal(new[] { "1", "2" }, result.Select(m => m.MessageId));
    }

    [Fact]
    public void NonGmailDistinctMessagesAllRetained()
    {
        var input = new[]
        {
            Msg("<1@m>", "INBOX", uid: "1"),
            Msg("<2@m>", "INBOX", uid: "2"),
            Msg("<3@m>", "INBOX", uid: "3"),
        };

        var result = MessageDeduplicator.CollapseForAggregate(input, KindByFolder(GmailFolders));

        Assert.Equal(3, result.Count);
    }
}
