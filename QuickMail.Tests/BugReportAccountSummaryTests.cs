// The account line in a bug report's Environment section — issue #639, found while triaging #637
// (offline drafts), where "Graph or IMAP?" was unanswerable from the report and cost a source read.
//
// Two things are locked here: that the line says which protocols are in use, and that it says
// NOTHING else. The string is published verbatim into a public GitHub issue, so an address, host
// name, or display name leaking into it is a privacy defect, not a formatting nit — hence the
// negative assertions, which are the point of the file rather than padding.

using System;
using System.Collections.Generic;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class BugReportAccountSummaryTests
{
    private static AccountModel Acct(BackendKind kind, bool shared = false) => new()
    {
        Id           = Guid.NewGuid(),
        BackendKind  = kind,
        IsShared     = shared,
        Username     = "someone@example.com",
        DisplayName  = "Someone Private",
        ImapHost     = "mail.private-host.example",
        Pop3Host     = "pop.private-host.example",
    };

    [Fact]
    public void DescribeAccounts_ReportsCountAndKind_ForOneAccount()
        => Assert.Equal("1 (IMAP)", MainViewModel.DescribeAccounts([Acct(BackendKind.ImapSmtp)]));

    [Fact]
    public void DescribeAccounts_NamesGraphAsMicrosoft365()
        => Assert.Equal("1 (Microsoft 365)", MainViewModel.DescribeAccounts([Acct(BackendKind.MicrosoftGraph)]));

    [Fact]
    public void DescribeAccounts_NamesPop3()
        => Assert.Equal("1 (POP3)", MainViewModel.DescribeAccounts([Acct(BackendKind.Pop3Smtp)]));

    [Fact]
    public void DescribeAccounts_CountsEveryAccount_ButListsEachKindOnce()
    {
        var result = MainViewModel.DescribeAccounts(
            [Acct(BackendKind.ImapSmtp), Acct(BackendKind.ImapSmtp), Acct(BackendKind.MicrosoftGraph)]);

        Assert.Equal("3 (IMAP, Microsoft 365)", result);
    }

    /// <summary>
    /// Enum order, not account order: the same setup must produce the same line every time, or two
    /// reports from one user read as two different configurations.
    /// </summary>
    [Fact]
    public void DescribeAccounts_OrdersKindsIndependentlyOfAccountOrder()
    {
        var oneWay   = MainViewModel.DescribeAccounts([Acct(BackendKind.Pop3Smtp), Acct(BackendKind.ImapSmtp)]);
        var otherWay = MainViewModel.DescribeAccounts([Acct(BackendKind.ImapSmtp), Acct(BackendKind.Pop3Smtp)]);

        Assert.Equal(oneWay, otherWay);
        Assert.Equal("2 (IMAP, POP3)", oneWay);
    }

    [Fact]
    public void DescribeAccounts_ReturnsZero_WhenNoAccountsConfigured()
    {
        Assert.Equal("0", MainViewModel.DescribeAccounts([]));
        Assert.Equal("0", MainViewModel.DescribeAccounts(null));
    }

    /// <summary>
    /// A shared mailbox is not one of the user's own accounts (#31) — it is a mailbox someone else
    /// shared with one of them, read through that account's token. Folding them into a single count
    /// reported three "accounts" for one account and two shared mailboxes, which is both misleading
    /// and a wasted triage signal, since a shared mailbox diverges from an ordinary account.
    /// </summary>
    [Fact]
    public void DescribeAccounts_CountsSharedMailboxesSeparately()
    {
        var result = MainViewModel.DescribeAccounts(
        [
            Acct(BackendKind.MicrosoftGraph),
            Acct(BackendKind.MicrosoftGraph, shared: true),
            Acct(BackendKind.MicrosoftGraph, shared: true),
        ]);

        Assert.Equal("1 (Microsoft 365), plus 2 shared mailboxes", result);
    }

    [Fact]
    public void DescribeAccounts_SaysOneSharedMailboxInTheSingular()
    {
        var result = MainViewModel.DescribeAccounts(
            [Acct(BackendKind.ImapSmtp), Acct(BackendKind.ImapSmtp, shared: true)]);

        Assert.Equal("1 (IMAP), plus 1 shared mailbox", result);
    }

    [Fact]
    public void DescribeAccounts_SaysNothingAboutSharedMailboxes_WhenThereAreNone()
        => Assert.DoesNotContain("shared", MainViewModel.DescribeAccounts([Acct(BackendKind.ImapSmtp)]));

    /// <summary>
    /// The redaction boundary. This text goes into a public issue body, so nothing that identifies
    /// the user or their servers may appear in it — whatever else the account object is carrying.
    /// </summary>
    [Fact]
    public void DescribeAccounts_LeaksNoAddressHostOrDisplayName()
    {
        var result = MainViewModel.DescribeAccounts(
            [Acct(BackendKind.ImapSmtp), Acct(BackendKind.MicrosoftGraph), Acct(BackendKind.Pop3Smtp)]);

        Assert.DoesNotContain("example.com", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-host", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Someone", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@", result, StringComparison.Ordinal);
    }
}
