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
        StubOAuthService? oauth = null, StubLocalStoreService? store = null,
        IAccountService? accounts = null)
    {
        var gate = new StubFeatureGate
        {
            [FeatureFlag.GraphBackend] = graph,
            [FeatureFlag.MicrosoftGraphMigration] = migration,
        };
        var vm = new AccountManagerViewModel(
            accounts ?? new StubAccountService(), new StubCredentialService(), new StubImapMailService(),
            oauth ?? new StubOAuthService(), store ?? new StubLocalStoreService(), new StubConfigService(),
            gate, Catalog);
        vm.Accounts.Add(selected);
        vm.SelectedAccount = selected;
        vm.ConfirmConvertToGraph = _ => true;   // default: user says yes; overridden per test
        return vm;
    }

    // ── Unsent drafts block the conversion ───────────────────────────────────────

    private static StubLocalStoreService HoldingADraft(Guid accountId)
    {
        var store = new StubLocalStoreService();
        store.SeededSummaries[(accountId, "Drafts")] =
        [
            new MailMessageSummary
            {
                MessageId = "local-1", AccountId = accountId, FolderName = "Drafts",
                Subject = "Airport thoughts", IsPendingUpload = true,
            },
        ];
        return store;
    }

    [Fact]
    public async Task Convert_RefusedWhileAnUnsentDraftIsHeld_LeavesTheAccountAlone()
    {
        // The refusal used to run AFTER the backend flip had been persisted, so it announced "was
        // not converted" about an account that was converted on disk -- and that finished
        // converting, unattended, on the next launch, destroying the very drafts it had just
        // promised to protect (#637).
        var account = MsImap();
        var vm = Vm(account, store: HoldingADraft(account.Id));

        await vm.ConvertToGraphCommand.ExecuteAsync(null);

        Assert.Equal(BackendKind.ImapSmtp, account.BackendKind);
        Assert.False(account.GraphConversionPending);
        Assert.Contains("was not converted", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Convert_RefusedWhileAnUnsentDraftIsHeld_DoesNotEvenAsk()
    {
        // Nothing below the confirmation can refuse safely, so the check runs above it -- which
        // also spares the user a question whose answer cannot be acted on.
        var account = MsImap();
        var vm = Vm(account, store: HoldingADraft(account.Id));
        var asked = false;
        vm.ConfirmConvertToGraph = _ => { asked = true; return true; };

        await vm.ConvertToGraphCommand.ExecuteAsync(null);

        Assert.False(asked);
    }

    [Fact]
    public async Task Convert_RefusedWhenTheStoreCannotBeAsked()
    {
        // Fails closed, like the removal confirmation it mirrors.
        var account = MsImap();
        var store = new StubLocalStoreService { CountUnsentMailFailure = new InvalidOperationException("database is locked") };
        var vm = Vm(account, store: store);

        await vm.ConvertToGraphCommand.ExecuteAsync(null);

        Assert.Equal(BackendKind.ImapSmtp, account.BackendKind);
        Assert.Contains("could not check", vm.StatusText, StringComparison.Ordinal);
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

    // ── The conversion marker is not this dialog's to write ───────────────────────

    /// <summary>An account service that actually round-trips, so a test can see what a save wrote and
    /// can stand in for another writer (MainViewModel) changing the file underneath the dialog.</summary>
    private sealed class RecordingAccountService : IAccountService
    {
        // Copies, not the caller's instances: the real service round-trips through JSON, so a loaded
        // account is a distinct object. Sharing references here would let a test mutate "the file" and
        // the VM's copy at once, which is precisely the confusion these tests exist to rule out.
        private static AccountModel Copy(AccountModel a) => new()
        {
            Id = a.Id, AccountName = a.AccountName, Username = a.Username,
            BackendKind = a.BackendKind, AuthType = a.AuthType,
            GraphConversionPending = a.GraphConversionPending,
        };

        public List<AccountModel> Stored { get; private set; } = [];
        public int SaveCount { get; private set; }
        public List<AccountModel> LoadAccounts() => [.. Stored.Select(Copy)];
        public void SaveAccounts(List<AccountModel> accounts) { SaveCount++; Stored = [.. accounts.Select(Copy)]; }
        public void SetDefaultAccount(Guid accountId) { }
    }

    [Fact]
    public async Task Convert_PersistsTheMarkerAsSet() // the one place allowed to RAISE it
    {
        var acct = MsImap();
        var svc = new RecordingAccountService();
        var vm = Vm(acct, accounts: svc);

        await vm.ConvertToGraphCommand.ExecuteAsync(null);

        var written = Assert.Single(svc.Stored.Where(a => a.Id == acct.Id));
        Assert.True(written.GraphConversionPending);
        Assert.Equal(BackendKind.MicrosoftGraph, written.BackendKind);
    }

    [Fact]
    public async Task ALaterSave_DoesNotResurrectAMarkerClearedElsewhere()
    {
        var acct = MsImap();
        var svc = new RecordingAccountService();
        var vm = Vm(acct, accounts: svc);
        await vm.ConvertToGraphCommand.ExecuteAsync(null);
        Assert.True(acct.GraphConversionPending);   // the dialog's copy still says "converting"

        // MainViewModel finishes the conversion and clears the marker on disk while this dialog is
        // still open — it owns the re-sync, not the dialog.
        foreach (var stored in svc.Stored) stored.GraphConversionPending = false;

        // Any later save from the dialog (here: the account Save button) must not write our stale copy
        // back over that. Doing so would cost a full redundant re-download on the next launch.
        var savesBefore = svc.SaveCount;
        vm.SaveAccountCommand.Execute(null);
        Assert.True(svc.SaveCount > savesBefore, "the Save command did not reach a save");

        Assert.All(svc.Stored, a => Assert.False(a.GraphConversionPending));
        Assert.False(acct.GraphConversionPending);   // and our copy is reconciled, not just the file
    }

    [Fact]
    public void SaveOfAnAccountTheFileDoesNotKnow_KeepsItsOwnMarker()
    {
        // A brand-new account has no persisted row to read the marker from; the save must keep what the
        // in-memory model says rather than defaulting it away.
        var acct = MsImap();
        acct.GraphConversionPending = true;
        var svc = new RecordingAccountService();   // empty store
        var vm = Vm(acct, accounts: svc);

        vm.SaveAccountCommand.Execute(null);

        Assert.True(Assert.Single(svc.Stored.Where(a => a.Id == acct.Id)).GraphConversionPending);
    }
}
