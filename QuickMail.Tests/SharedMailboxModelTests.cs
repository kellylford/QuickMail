using System;
using System.Text.Json;
using QuickMail.Models;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Pins the shared-mailbox linkage fields on <see cref="AccountModel"/> (#31, PR 1): they persist
/// through the same JSON serialization accounts.json uses, a normal account is unaffected (no
/// migration), and the accessible name inserts the "shared mailbox" qualifier exactly where §7 says.
/// </summary>
public class SharedMailboxModelTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static AccountModel RoundTrip(AccountModel a) =>
        JsonSerializer.Deserialize<AccountModel>(JsonSerializer.Serialize(a, JsonOptions), JsonOptions)!;

    [Fact]
    public void SharedFields_RoundTripThroughJson()
    {
        var parentId = Guid.NewGuid();
        var back = RoundTrip(new AccountModel
        {
            AccountName = "Support",
            Username = "support@bits-acb.org",
            BackendKind = BackendKind.MicrosoftGraph,
            IsShared = true,
            ParentAccountId = parentId,
            SharedAddress = "support@bits-acb.org",
        });

        Assert.True(back.IsShared);
        Assert.Equal(parentId, back.ParentAccountId);
        Assert.Equal("support@bits-acb.org", back.SharedAddress);
        Assert.Equal(BackendKind.MicrosoftGraph, back.BackendKind);
    }

    [Fact]
    public void NormalAccount_DefaultsUnshared_NoMigration()
    {
        var back = RoundTrip(new AccountModel { AccountName = "Idea Place", Username = "tim@icanbrew.com" });

        Assert.False(back.IsShared);
        Assert.Null(back.ParentAccountId);
        Assert.Null(back.SharedAddress);
        Assert.False(back.NotifyOnNewMail);   // #31 PR 5: notify opt-in defaults off, no migration
    }

    [Fact]
    public void ManagerListAccessibleName_QualifiesSharedMailbox() // #31 PR 5
    {
        var shared = new AccountModel { AccountName = "Support", Username = "support@work.com", IsShared = true };
        var normal = new AccountModel { AccountName = "Work", Username = "me@work.com" };
        var sharedDefault = new AccountModel { AccountName = "Support", IsShared = true, IsDefault = true };

        Assert.Equal("Support, shared mailbox", shared.ManagerListAccessibleName);
        Assert.Equal("Work", normal.ManagerListAccessibleName);                       // no qualifier for a normal account
        Assert.Equal("Support, shared mailbox - default", sharedDefault.ManagerListAccessibleName);
        // Deliberately no connection/unread state — unlike AccessibleName — so the editor list stays quiet.
        Assert.DoesNotContain("connected", shared.ManagerListAccessibleName);
    }

    [Fact]
    public void NotifyOnNewMail_RoundTripsThroughJson() // #31 PR 5
    {
        var back = RoundTrip(new AccountModel
        {
            AccountName = "Support", Username = "support@bits-acb.org",
            BackendKind = BackendKind.MicrosoftGraph, IsShared = true,
            SharedAddress = "support@bits-acb.org", NotifyOnNewMail = true,
        });

        Assert.True(back.NotifyOnNewMail);
    }

    [Fact]
    public void AccessibleName_Shared_InsertsQualifierAfterLabel()
    {
        var shared = new AccountModel { AccountName = "Support", IsShared = true, IsConnected = true, TotalUnread = 12 };
        Assert.Equal("Support, shared mailbox, connected, 12 unread", shared.AccessibleName);

        shared.TotalUnread = 0;
        Assert.Equal("Support, shared mailbox, connected", shared.AccessibleName);

        shared.IsConnected = false;
        Assert.Equal("Support, shared mailbox, disconnected", shared.AccessibleName);
    }

    [Fact]
    public void AccessibleName_Normal_Unchanged()
    {
        var normal = new AccountModel { AccountName = "Kelly", IsConnected = true, TotalUnread = 1630 };
        Assert.Equal("Kelly, connected, 1630 unread", normal.AccessibleName);

        normal.IsConnected = false;
        Assert.Equal("Kelly, disconnected", normal.AccessibleName);
    }
}
