using System.Threading.Tasks;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Covers the MainViewModel disposal contract added for the Fable5CR review:
/// Dispose cancels/releases all operation CTS slots and must be safe to call
/// at any point in the VM lifecycle, including twice.
/// </summary>
public class MainViewModelLifecycleTests
{
    private static MainViewModel MakeVm() => new(
        new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
        new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
        new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
        new StubRuleService(), new StubSmtpService(),
        uiDispatcher: new StubUiDispatcher());

    [Fact]
    public void Dispose_BeforeAnyLoad_DoesNotThrow()
    {
        var vm = MakeVm();
        vm.Dispose();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var vm = MakeVm();
        vm.Dispose();
        vm.Dispose();
    }

    [Fact]
    public async Task Dispose_AfterInitialLoad_DoesNotThrow()
    {
        var vm = MakeVm();
        await vm.InitialLoadAsync();
        vm.Dispose();
        vm.Dispose();
    }
}
