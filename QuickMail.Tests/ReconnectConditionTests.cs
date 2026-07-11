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
}
