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
    private static AddAccountViewModel Make(StubOAuthService oauth) =>
        new(new StubFeatureGate { [FeatureFlag.GraphBackend] = true },
            new StubImapMailService(), oauth, new ProviderCatalog());

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
}
