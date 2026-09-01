// What may and may not erase the sentence on the compose window's notice field — issue #637.
//
// The field is the only durable, focusable account of why a save or a send did not happen, and for
// a refused draft it carries what the server said. The window's field is bound to the string, so
// clearing it collapses the field and takes focus with it.
//
// Two clearing rules live here, and the round that replaced a sticky bool with a re-evaluated
// condition collapsed them into one:
//
//   - a SAVE that succeeds disproves a sentence describing a moment ("could not write this to your
//     computer"), so a notice with no condition on it is cleared by the next success;
//   - an EDIT disproves nothing. It can only resolve a condition recorded alongside the sentence.
//     Treating "no condition" as "resolved" here meant one character typed into To erased what the
//     server had said -- the sentence the user opened the draft to read, and the one the user guide
//     tells him to act on.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class DraftNoticeSurvivalTests
{
    private const string ServerSaid = "Your mail server refused it: over quota.";

    private static ComposeViewModel Compose(ILocalDraftService? drafts = null,
                                            ICredentialService? credentials = null,
                                            IMailService? mail = null) => new(
        new StubSmtpService(), new StubAccountService(), credentials ?? new StubCredentialService(),
        mail ?? new RecordingMailService { AppendDraftThrows = true },
        drafts ?? new PlainDraftService(), new StubTemplateService());

    private static AccountModel Account(AuthType auth = AuthType.OAuth2Google) => new()
    {
        Id = Guid.NewGuid(), Username = "samuel@interfree.ca", AuthType = auth,
    };

    private static ComposeModel RefusedDraft(string to) => new()
    {
        AccountId       = Guid.NewGuid(),
        DraftMessageId  = "local-1",
        DraftFolderName = "Drafts",
        DeliveryNotice  = ServerSaid,
        To              = to,
        Subject         = "Airport thoughts",
        Body            = "Boarding soon.",
    };

    [Fact]
    public void OpeningARefusedDraft_StillShowsWhatTheServerSaid()
    {
        // Seed writes the notice and then writes To, so this failed before the window was ever
        // shown: the reason was gone by the time the user arrived, for every refused draft that had
        // a recipient -- which is very nearly all of them.
        var vm = Compose();

        vm.Seed(RefusedDraft("someone@example.com"));

        Assert.Equal(ServerSaid, vm.DeliveryNotice);
    }

    [Fact]
    public void EditingTheRecipientOfARefusedDraft_KeepsWhatTheServerSaid()
    {
        // Editing To is very often the fix the server asked for, so this is the keystroke most
        // likely to arrive -- and it took the instruction away as the user began to follow it.
        var vm = Compose();
        vm.Seed(RefusedDraft(string.Empty));

        vm.To = "someone@example.com";

        Assert.Equal(ServerSaid, vm.DeliveryNotice);
    }

    [Fact]
    public async Task EditingTheRecipient_KeepsALiveStoreFailure()
    {
        // The store is still broken; nothing about typing a recipient makes the message saved.
        var vm = Compose(new ThrowingDraftService());
        vm.SenderAccount = Account();
        vm.To      = "someone@example.com";
        vm.Subject = "Airport thoughts";
        await vm.AutoSaveAsync();
        Assert.Contains("could not write this message to your computer", vm.DeliveryNotice,
            StringComparison.Ordinal);

        vm.To = "someone.else@example.com";

        Assert.Contains("could not write this message to your computer", vm.DeliveryNotice,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task EditingTheRecipient_KeepsTheNoDraftsFolderRefusal()
    {
        // The window refuses to close while this stands, so erasing it leaves a user pressing the
        // close key at a window that will not close and will not say why.
        // Both sources of a folder name have to come up empty: the cached list, and the server.
        var vm = Compose(new NoDraftsFolderService(), mail: new StubImapMailService());
        vm.SenderAccount = Account();
        vm.To      = "someone@example.com";
        vm.Subject = "Airport thoughts";
        await vm.SaveDraftCommand.ExecuteAsync(null);
        Assert.Contains("no Drafts folder", vm.DeliveryNotice, StringComparison.Ordinal);

        vm.To = "someone.else@example.com";

        Assert.Contains("no Drafts folder", vm.DeliveryNotice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EditingTheRecipient_DoesRetireARefusalThatAskedForOne()
    {
        // The other half of the rule: a refusal that named a condition goes as soon as the user
        // resolves it. Leaving it standing after he has done what it asked is the same defect the
        // other way round.
        var vm = Compose();
        vm.SenderAccount = Account();
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Contains("no recipient", vm.DeliveryNotice, StringComparison.Ordinal);

        vm.To = "someone@example.com";

        Assert.Equal(string.Empty, vm.DeliveryNotice);
    }

    [Fact]
    public async Task ChoosingASenderAccount_RetiresTheRefusalThatAskedForOne()
    {
        // Doing what the sentence asked, on the field the sentence named. Only To was wired up, so
        // the user who followed this instruction exactly was the one left looking at it.
        var vm = Compose();
        vm.To = "someone@example.com";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Contains("no sender account", vm.DeliveryNotice, StringComparison.Ordinal);

        vm.SenderAccount = Account();

        Assert.Equal(string.Empty, vm.DeliveryNotice);
    }

    [Fact]
    public async Task ASaveThatSucceeds_StillClearsASentenceWithNoConditionOnIt()
    {
        // The rule an edit must NOT follow, kept for the path it was written for.
        var store = new FailsUntilToldNotTo();
        var vm = Compose(store);
        vm.SenderAccount = Account();
        vm.To      = "someone@example.com";
        vm.Subject = "Airport thoughts";

        await vm.SaveDraftCommand.ExecuteAsync(null);
        Assert.Contains("could not be saved", vm.DeliveryNotice, StringComparison.Ordinal);

        store.Fail = false;
        await vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.DeliveryNotice);
    }

    [Fact]
    public async Task ACredentialStoreThatThrows_DoesNotTakeTheWindowWithIt()
    {
        // Re-testing the password refusal reads the Windows credential store, and it now does so
        // from a property setter -- a binding's source update, on the UI thread. An unreadable
        // store is also not evidence the password is there, so the refusal stands.
        var vm = Compose(credentials: new ThrowsAfterFirstRead());
        vm.To = "someone@example.com";
        vm.SenderAccount = Account(AuthType.Password);
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Contains("no stored password", vm.DeliveryNotice, StringComparison.Ordinal);

        vm.To = "someone.else@example.com";

        Assert.Contains("no stored password", vm.DeliveryNotice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetestingThePasswordRefusal_DoesNotReadTheCredentialStorePerKeystroke()
    {
        // Measured at 24 reads for 24 characters before this: synchronous Windows credential-store
        // calls on the UI thread, from a binding's source update, for every character typed.
        var creds = new CountingCredentials();
        var vm = Compose(credentials: creds);
        vm.To = "a@b.c";
        vm.SenderAccount = Account(AuthType.Password);
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Contains("no stored password", vm.DeliveryNotice, StringComparison.Ordinal);

        var before = creds.Reads;
        foreach (var ch in "someone.else@example.com")
            vm.To += ch;

        // One read for the first keystroke -- Send reads the store directly, so the cache starts
        // empty -- and none for the twenty-three after it.
        Assert.Equal(before + 1, creds.Reads);
        Assert.Contains("no stored password", vm.DeliveryNotice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangingAccount_AsksTheCredentialStoreAgain()
    {
        // The cached answer is about one account, so it must not survive the account changing --
        // otherwise the refusal would stand for an account that does have a password stored.
        var creds = new CountingCredentials();
        var vm = Compose(credentials: creds);
        vm.To = "a@b.c";
        vm.SenderAccount = Account(AuthType.Password);
        await vm.SendCommand.ExecuteAsync(null);

        var before = creds.Reads;
        vm.SenderAccount = Account(AuthType.Password);

        Assert.True(creds.Reads > before);
    }

    [Fact]
    public async Task AStoreThatThrowsOnce_DoesNotPinTheRefusalForEver()
    {
        // The cache was written to hold the CATCH as well as a real answer, so one credential-store
        // hiccup made "no stored password" permanent for the life of the window, on the durable
        // field, with no way back.
        var creds = new ThrowsOnceThenAnswers();
        var vm = Compose(credentials: creds);
        vm.To = "a@b.c";
        vm.SenderAccount = Account(AuthType.Password);
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Contains("no stored password", vm.DeliveryNotice, StringComparison.Ordinal);

        creds.Throw = true;
        vm.To = "someone@example.com";          // asks, throws, refusal stands, nothing cached
        Assert.Contains("no stored password", vm.DeliveryNotice, StringComparison.Ordinal);

        creds.Throw = false;
        creds.Password = "hunter2";             // the user has signed in again
        vm.To = "someone.else@example.com";

        Assert.Equal(string.Empty, vm.DeliveryNotice);
    }

    [Fact]
    public async Task ASuccessfulSave_AsksTheCredentialStoreAgain()
    {
        // Signing in again in Manage Accounts changes nothing this window can observe, so the
        // cached answer kept the refusal standing through an edit AND through a successful save --
        // the user having done exactly what the sentence asked.
        var creds = new ThrowsOnceThenAnswers();
        var vm = Compose(credentials: creds);
        vm.To = "someone@example.com";
        vm.Subject = "Airport thoughts";
        vm.SenderAccount = Account(AuthType.Password);
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Contains("no stored password", vm.DeliveryNotice, StringComparison.Ordinal);

        // An edit is what puts the answer in the cache: Send reads the store directly, so without
        // this the save would find an empty cache and read afresh whatever the rule was.
        vm.To = "someone.else@example.com";
        Assert.Contains("no stored password", vm.DeliveryNotice, StringComparison.Ordinal);

        creds.Password = "hunter2";             // the user has signed in again in Manage Accounts
        await vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.DeliveryNotice);
    }

    private sealed class ThrowsOnceThenAnswers : ICredentialService
    {
        public bool Throw { get; set; }
        public string? Password { get; set; }
        public string? GetPassword(Guid accountId)
        {
            if (Throw) throw new System.ComponentModel.Win32Exception("credential store unavailable");
            return Password;
        }
        public void SavePassword(Guid accountId, string password) { }
        public void DeletePassword(Guid accountId) { }
        public void SaveSecret(string key, string value) { }
        public string? GetSecret(string key) => null;
        public void DeleteSecret(string key) { }
    }

    private sealed class CountingCredentials : ICredentialService
    {
        public int Reads { get; private set; }
        public string? GetPassword(Guid accountId) { Reads++; return null; }
        public void SavePassword(Guid accountId, string password) { }
        public void DeletePassword(Guid accountId) { }
        public void SaveSecret(string key, string value) { }
        public string? GetSecret(string key) => null;
        public void DeleteSecret(string key) { }
    }

    // ── stubs ────────────────────────────────────────────────────────────────

    private class PlainDraftService : ILocalDraftService
    {
        public virtual Task<PendingDraftSave> SaveAsync(AccountModel account, ComposeModel draft,
            string folderName, string? previousMessageId, CancellationToken ct = default)
            => Task.FromResult(new PendingDraftSave("local-1", null));
        public virtual Task<string?> ResolveDraftsFolderNameAsync(Guid accountId)
            => Task.FromResult<string?>("Drafts");
        public Task<ComposeModel?> LoadAsync(Guid a, string f, string id, CancellationToken ct = default)
            => Task.FromResult<ComposeModel?>(null);
        public Task<string?> GetSupersededServerIdAsync(Guid a, string f, string id)
            => Task.FromResult<string?>(null);
        public Task MarkSendFailedAsync(Guid a, string f, string id, string reason) => Task.CompletedTask;
        public Task DiscardAsync(Guid a, string f, string id) => Task.CompletedTask;
        public Task<IReadOnlyList<MailMessageSummary>> GetPendingAsync(Guid a)
            => Task.FromResult<IReadOnlyList<MailMessageSummary>>([]);
        public Task<string> ReadDeliveryNoticeAsync(Guid a, string f, string id)
            => Task.FromResult(string.Empty);
    }

    private sealed class ThrowingDraftService : PlainDraftService
    {
        public override Task<PendingDraftSave> SaveAsync(AccountModel account, ComposeModel draft,
            string folderName, string? previousMessageId, CancellationToken ct = default)
            => throw new InvalidOperationException("database is locked");
    }

    private sealed class NoDraftsFolderService : PlainDraftService
    {
        public override Task<string?> ResolveDraftsFolderNameAsync(Guid accountId)
            => Task.FromResult<string?>(null);
    }

    private sealed class FailsUntilToldNotTo : PlainDraftService
    {
        public bool Fail { get; set; } = true;
        public override Task<PendingDraftSave> SaveAsync(AccountModel account, ComposeModel draft,
            string folderName, string? previousMessageId, CancellationToken ct = default)
            => Fail ? throw new InvalidOperationException("database is locked")
                    : Task.FromResult(new PendingDraftSave("local-1", null));
    }

    private sealed class ThrowsAfterFirstRead : ICredentialService
    {
        private bool _read;
        public string? GetPassword(Guid accountId)
        {
            if (_read) throw new System.ComponentModel.Win32Exception("credential store unavailable");
            _read = true;
            return null;                       // the refusal the test then re-tests
        }
        public void SavePassword(Guid accountId, string password) { }
        public void DeletePassword(Guid accountId) { }
        public void SaveSecret(string key, string value) { }
        public string? GetSecret(string key) => null;
        public void DeleteSecret(string key) { }
    }
}
