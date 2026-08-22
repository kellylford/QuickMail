using System;
using System.Linq;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Locks the #219 reconnect condition (`MainViewModel.AccountsNeedingConnect`): an account is
/// reconnected when the backend has dropped it — even though the VM's `_cachedFolders` still has it —
/// which is exactly the mid-session re-consent case. Also covers the newly-added (not-cached) case
/// and the already-healthy (skip) case.
/// </summary>
public class ReconnectConditionTests
{
    private static AccountModel Acct(Guid id) => new() { Id = id };

    [Fact]
    public void BackendDropped_ButStillCached_IsReconnected() // the #219 case
    {
        var id = Guid.NewGuid();
        var result = MainViewModel.AccountsNeedingConnect(
            [Acct(id)],
            isBackendConnected: _ => false, // backend dropped it (re-consent left it unregistered)
            hasCachedFolders: _ => true);   // but the VM still shows its folders
        Assert.Contains(result, a => a.Id == id);
    }

    [Fact]
    public void ConnectedAndCached_IsSkipped()
    {
        var id = Guid.NewGuid();
        var result = MainViewModel.AccountsNeedingConnect([Acct(id)], _ => true, _ => true);
        Assert.Empty(result);
    }

    [Fact]
    public void NotCached_IsReconnected() // newly added account
    {
        var id = Guid.NewGuid();
        var result = MainViewModel.AccountsNeedingConnect([Acct(id)], _ => true, _ => false);
        Assert.Contains(result, a => a.Id == id);
    }

    [Fact]
    public void GraphParentSharedMailbox_IsConnected() // #31 PR 2
    {
        // A Graph-parent shared mailbox borrows the parent's token and reads /users/{SharedAddress}, so
        // it now connects like any account (it was skipped in PR 1).
        var id = Guid.NewGuid();
        var shared = new AccountModel { Id = id, IsShared = true, BackendKind = BackendKind.MicrosoftGraph };
        var result = MainViewModel.AccountsNeedingConnect([shared], _ => false, _ => false);
        Assert.Contains(result, a => a.Id == id);
    }

    [Fact]
    public void ImapParentSharedMailbox_IsNotConnected() // #31 — shared is Graph-only (IMAP PR 3 dropped)
    {
        // Shared mailboxes are Graph-only for v1; an IMAP-backed shared account should never be created
        // (parent eligibility is Graph-only), and if one somehow exists it must not connect.
        var id = Guid.NewGuid();
        var shared = new AccountModel { Id = id, IsShared = true, BackendKind = BackendKind.ImapSmtp };
        var result = MainViewModel.AccountsNeedingConnect([shared], _ => false, _ => false);
        Assert.DoesNotContain(result, a => a.Id == id);
    }
}
