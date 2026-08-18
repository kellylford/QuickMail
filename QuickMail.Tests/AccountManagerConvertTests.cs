using System;
using System.Linq;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// #529 step 4 (PR 1): the opt-in "Convert to Microsoft 365 (Graph)" command's gating and its safe half
/// of the sequence — token BEFORE any destructive step, then the crash-safe marker + backend flip + cache
/// purge. The in-session re-sync and the folder-reference remap are the follow-up; this pins that the
/// command never purges without a Graph token and always resolves Graph (not IMAP) scopes.
/// </summary>
public class AccountManagerConvertTests
{
    private static readonly ProviderCatalog Catalog = new();

    private static AccountModel MsImap(string user = "me@outlook.com", bool? personal = null) => new()
    {
        Id = Guid.NewGuid(), AccountName = user, Username = user,
        BackendKind = BackendKind.ImapSmtp, AuthType = AuthType.OAuth2Microsoft,
        IsPersonalMicrosoftAccount = personal,
    };

    private static AccountManagerViewModel Vm(
        AccountModel selected, bool migration = true, bool graph = true,
        StubOAuthService? oauth = null, StubLocalStoreService? store = null)
    {
        var gate = new StubFeatureGate
        {
            [FeatureFlag.GraphBackend] = graph,
            [FeatureFlag.MicrosoftGraphMigration] = migration,
        };
        var vm = new AccountManagerViewModel(
            new StubAccountService(), new StubCredentialService(), new StubImapMailService(),
            oauth ?? new StubOAuthService(), store ?? new StubLocalStoreService(), new StubConfigService(),
            gate, Catalog);
        vm.Accounts.Add(selected);
        vm.SelectedAccount = selected;
        vm.ConfirmConvertToGraph = _ => true;   // default: user says yes; overridden per test
        return vm;
    }

    // ── Gating ────────────────────────────────────────────────────────────────────

    [Fact]
    public void CanConvert_MicrosoftImapOAuth_BothFlagsOn()
        => Assert.True(Vm(MsImap()).CanConvertToGraph);

    [Fact]
    public void CanConvert_False_WhenMigrationFlagOff()
        => Assert.False(Vm(MsImap(), migration: false).CanConvertToGraph);

    [Fact]
    public void CanConvert_False_WhenGraphBackendOff()
        => Assert.False(Vm(MsImap(), graph: false).CanConvertToGraph);

    [Fact]
    public void CanConvert_False_ForAGraphAccount() // already converted
        => Assert.False(Vm(new AccountModel { Id = Guid.NewGuid(), BackendKind = BackendKind.MicrosoftGraph, AuthType = AuthType.OAuth2Microsoft }).CanConvertToGraph);

    [Fact]
    public void CanConvert_False_ForAPasswordAccount()
        => Assert.False(Vm(new AccountModel { Id = Guid.NewGuid(), BackendKind = BackendKind.ImapSmtp, AuthType = AuthType.Password }).CanConvertToGraph);

    [Fact]
    public void CanConvert_False_ForAGoogleAccount()
        => Assert.False(Vm(new AccountModel { Id = Guid.NewGuid(), BackendKind = BackendKind.ImapSmtp, AuthType = AuthType.OAuth2Google }).CanConvertToGraph);

    [Fact]
    public void CanConvert_False_ForASharedMailbox()
        => Assert.False(Vm(new AccountModel { Id = Guid.NewGuid(), BackendKind = BackendKind.ImapSmtp, AuthType = AuthType.OAuth2Microsoft, IsShared = true }).CanConvertToGraph);

    // ── The sequence ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Convert_Success_FlipsMarksAndPurges()
    {
        var acct = MsImap();
        var store = new StubLocalStoreService();
        var vm = Vm(acct, store: store);

        await vm.ConvertToGraphCommand.ExecuteAsync(null);

        Assert.Equal(BackendKind.MicrosoftGraph, acct.BackendKind);
        Assert.True(acct.GraphConversionPending);                       // crash-safe marker set
        Assert.Contains(acct.Id, store.ClearedMailAccountIds);          // cache purged
        Assert.True(store.SeededFolders.TryGetValue(acct.Id, out var f) && f.Count == 0); // folder rows cleared
    }

    [Fact]
    public async Task Convert_Success_HandsOffToTheSyncLoop()
    {
        var acct = MsImap();
        var vm = Vm(acct);
        Guid? handedOff = null;
        vm.AccountConvertedToGraph = id => { handedOff = id; return Task.CompletedTask; };

        await vm.ConvertToGraphCommand.ExecuteAsync(null);

        Assert.Equal(acct.Id, handedOff);   // the main sync loop is told, in-session (the #454 safeguard)
    }

    [Fact]
    public async Task Convert_TokenFails_NoHandoff()
    {
        var acct = MsImap();
        var vm = Vm(acct, oauth: new StubOAuthService { ThrowOnGetAccessToken = new Exception("declined") });
        var handedOff = false;
        vm.AccountConvertedToGraph = _ => { handedOff = true; return Task.CompletedTask; };

        await vm.ConvertToGraphCommand.ExecuteAsync(null);

        Assert.False(handedOff);
    }

    [Fact]
    public async Task Convert_TokenFails_LeavesAccountEntirelyOnImap()
    {
        var acct = MsImap();
        var store = new StubLocalStoreService();
        var oauth = new StubOAuthService { ThrowOnGetAccessToken = new Exception("user declined consent") };
        var vm = Vm(acct, oauth: oauth, store: store);

        await vm.ConvertToGraphCommand.ExecuteAsync(null);

        Assert.Equal(BackendKind.ImapSmtp, acct.BackendKind);           // untouched
        Assert.False(acct.GraphConversionPending);                     // no marker
        Assert.Empty(store.ClearedMailAccountIds);                     // never purged
        Assert.DoesNotContain(acct.Id, store.SeededFolders.Keys);      // folders not cleared
    }

    [Fact]
    public async Task Convert_Declined_DoesNothing()
    {
        var acct = MsImap();
        var store = new StubLocalStoreService();
        var vm = Vm(acct, store: store);
        vm.ConfirmConvertToGraph = _ => false;   // user says no

        await vm.ConvertToGraphCommand.ExecuteAsync(null);

        Assert.Equal(BackendKind.ImapSmtp, acct.BackendKind);
        Assert.Empty(store.ClearedMailAccountIds);
    }

    [Fact]
    public async Task Convert_NoConfirmCallback_FailsClosed()
    {
        var acct = MsImap();
        var store = new StubLocalStoreService();
        var vm = Vm(acct, store: store);
        vm.ConfirmConvertToGraph = null;   // View didn't wire it

        await vm.ConvertToGraphCommand.ExecuteAsync(null);

        Assert.Equal(BackendKind.ImapSmtp, acct.BackendKind);
        Assert.Empty(store.ClearedMailAccountIds);
    }

    // ── Explicit Graph scopes (the trap Kelly caught) ─────────────────────────────
    // DefaultScopesFor would return IMAP scopes here (BackendKind is still ImapSmtp), so the token must
    // be requested with the explicit Graph scope set, chosen personal-vs-work.

    [Fact]
    public async Task Convert_PersonalAccount_RequestsPersonalGraphScopes()
    {
        var oauth = new StubOAuthService();
        var vm = Vm(MsImap("me@outlook.com", personal: true), oauth: oauth);

        await vm.ConvertToGraphCommand.ExecuteAsync(null);

        Assert.Same(OAuthService.GraphMailScopesPersonal, oauth.LastAccessTokenScopes);
        Assert.NotSame(OAuthService.ImapSmtpScopes, oauth.LastAccessTokenScopes);
    }

    [Fact]
    public async Task Convert_WorkAccount_RequestsWorkSchoolGraphScopes()
    {
        var oauth = new StubOAuthService();
        var vm = Vm(MsImap("user@contoso.com", personal: false), oauth: oauth);

        await vm.ConvertToGraphCommand.ExecuteAsync(null);

        Assert.Same(OAuthService.GraphMailScopesWorkSchool, oauth.LastAccessTokenScopes);
    }
}
