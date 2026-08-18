using System.Linq;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Pins #527: a personal Microsoft account gets to Graph when the user picks it by hand. The
/// post-sign-in correction (`OnMicrosoftSignInCompleted`) exists to undo an <em>auto-inferred</em>
/// Graph choice on a vanity domain once the tenant id proves the account is personal — but it must
/// honor a choice the user made deliberately in Advanced, the same way `ChooseBackendForMicrosoftAccount`
/// already does. Its missing `_backendUserChosen` guard was the bug: it left personal-on-Graph
/// unreachable through the UI.
/// </summary>
public class PersonalGraphBackendChoiceTests
{
    private static AddAccountViewModel Make(StubOAuthService oauth, bool graphDefault = false) =>
        new(new StubFeatureGate { [FeatureFlag.GraphBackend] = true, [FeatureFlag.MicrosoftGraphDefault] = graphDefault },
            new StubImapMailService(), oauth, new ProviderCatalog());

    private static AddAccountViewModel Microsoft(string address, bool graphDefault, StubOAuthService? oauth = null)
    {
        var vm = Make(oauth ?? new StubOAuthService(), graphDefault);
        vm.SelectedProvider = new ProviderCatalog().ById(ProviderCatalog.MicrosoftId);
        vm.Username = address;
        return vm;
    }

    [Fact]
    public async Task PersonalAccount_UserPicksGraphByHand_SignInKeepsGraph()
    {
        var oauth = new StubOAuthService { SignInUsername = "me@outlook.com", SignInIsPersonalAccount = true };
        var vm = Make(oauth);
        vm.SelectedProvider = new ProviderCatalog().ById(ProviderCatalog.MicrosoftId);
        vm.Username = "me@outlook.com";

        // The user deliberately switches the connection method to Microsoft 365 (Graph) in Advanced.
        vm.SelectedBackend = vm.AvailableBackends.First(b => b.Kind == BackendKind.MicrosoftGraph);
        Assert.Equal(BackendKind.MicrosoftGraph, vm.BackendKind);

        await vm.SignInMicrosoftCommand.ExecuteAsync(null);

        // #527: a hand-picked Graph choice for a personal account survives sign-in — not reverted to IMAP.
        Assert.True(vm.IsPersonalMicrosoftAccount);
        Assert.Equal(BackendKind.MicrosoftGraph, vm.BackendKind);
    }

    [Fact]
    public async Task PersonalAccount_GraphAutoInferredFromDomain_SignInStillRevertsToImap()
    {
        var oauth = new StubOAuthService { SignInUsername = "me@myvanitydomain.com", SignInIsPersonalAccount = true };
        var vm = Make(oauth);
        vm.Username = "me@myvanitydomain.com";
        vm.SelectedProvider = new ProviderCatalog().ById(ProviderCatalog.MicrosoftId);

        // A vanity domain is not on the consumer list, so the address inference moves it to Graph on
        // its own (internal, NOT user-chosen) — the exact case the post-sign-in correction is for.
        vm.CommitUsername();
        Assert.Equal(BackendKind.MicrosoftGraph, vm.BackendKind);

        await vm.SignInMicrosoftCommand.ExecuteAsync(null);

        // Sign-in reveals a personal account → the auto-inferred Graph is corrected back to IMAP.
        // This behavior is unchanged by #527; only a hand-picked choice is now spared.
        Assert.Equal(BackendKind.ImapSmtp, vm.BackendKind);
    }

    // ── #529 step 3: MicrosoftGraphDefault makes Graph the default for new Microsoft accounts ──
    // Flag off = today's behavior (consumer → IMAP). Flag on = Graph for every Microsoft account,
    // personal included, with IMAP still hand-selectable and the personal→IMAP revert suppressed.

    [Fact]
    public void FlagOff_ConsumerAccount_DefaultsToImap() // regression pin of today's default
    {
        var vm = Microsoft("me@outlook.com", graphDefault: false);
        vm.CommitUsername();
        Assert.Equal(BackendKind.ImapSmtp, vm.BackendKind);
    }

    [Fact]
    public void FlagOn_ConsumerAccount_DefaultsToGraph()
    {
        var vm = Microsoft("me@outlook.com", graphDefault: true);
        vm.CommitUsername();
        Assert.Equal(BackendKind.MicrosoftGraph, vm.BackendKind);
    }

    [Fact]
    public void FlagOn_WorkAccount_StillDefaultsToGraph()
    {
        var vm = Microsoft("user@contoso.com", graphDefault: true);
        vm.CommitUsername();
        Assert.Equal(BackendKind.MicrosoftGraph, vm.BackendKind);
    }

    [Fact]
    public void GraphDefaultOn_ButGraphBackendOff_StaysImap() // the AND: Graph must be offered to be the default
    {
        var vm = new AddAccountViewModel(
            new StubFeatureGate { [FeatureFlag.GraphBackend] = false, [FeatureFlag.MicrosoftGraphDefault] = true },
            new StubImapMailService(), new StubOAuthService(), new ProviderCatalog());
        vm.SelectedProvider = new ProviderCatalog().ById(ProviderCatalog.MicrosoftId);
        vm.Username = "me@outlook.com";
        vm.CommitUsername();
        Assert.Equal(BackendKind.ImapSmtp, vm.BackendKind);
    }

    [Fact]
    public void FlagOn_HandPickedImapInAdvanced_Survives()
    {
        var vm = Microsoft("me@outlook.com", graphDefault: true);
        vm.CommitUsername();
        Assert.Equal(BackendKind.MicrosoftGraph, vm.BackendKind);   // flag defaulted it to Graph

        // The user switches the connection method to Standard IMAP/SMTP in Advanced — a real change
        // (Graph → IMAP), so _backendUserChosen is set and the default no longer applies.
        vm.SelectedBackend = vm.AvailableBackends.First(b => b.Kind == BackendKind.ImapSmtp);
        vm.CommitUsername();                                        // must not override the hand-pick
        Assert.Equal(BackendKind.ImapSmtp, vm.BackendKind);
    }

    [Fact]
    public async Task FlagOn_PersonalAccount_StaysOnGraphAfterSignIn()
    {
        var oauth = new StubOAuthService { SignInUsername = "me@outlook.com", SignInIsPersonalAccount = true };
        var vm = Microsoft("me@outlook.com", graphDefault: true, oauth);
        vm.CommitUsername();
        Assert.Equal(BackendKind.MicrosoftGraph, vm.BackendKind);

        await vm.SignInMicrosoftCommand.ExecuteAsync(null);

        // OnMicrosoftSignInCompleted is suppressed when the flag is on → the personal account is NOT
        // reverted to IMAP (contrast PersonalAccount_GraphAutoInferredFromDomain_SignInStillRevertsToImap,
        // which is the flag-off path).
        Assert.True(vm.IsPersonalMicrosoftAccount);
        Assert.Equal(BackendKind.MicrosoftGraph, vm.BackendKind);
    }
}
