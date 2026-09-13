using System;
using System.IO;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// #700 end to end: a rules file that can't be read is reported as that, not as having no rules, and nothing
/// saves over it. Before the fix the Rules Manager said "No client-side rules", and the next rule saved replaced
/// every rule in the file with itself.
/// </summary>
public sealed class UnreadableRulesFileTests : IDisposable
{
    private const string Damaged = "[{ \"Name\": \"Keep me\", \"SubjectContains\": \"x\" ";   // cut off mid-rule

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"qm-rules-700-{Guid.NewGuid():N}");
    private readonly string _path;

    public UnreadableRulesFileTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "rules.json");
        File.WriteAllText(_path, Damaged);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task TheRulesManager_SaysTheRulesCouldntBeLoaded_AndANewRuleLeavesTheFileAlone()
    {
        var account = new AccountModel { Id = Guid.NewGuid(), BackendKind = BackendKind.ImapSmtp, Username = "i@x.com", AccountName = "Home" };
        var rules = new RuleService(new StubImapMailService(), new StubLocalStoreService(), _dir);
        var vm = new UnifiedRulesViewModel(rules, serverRules: null, [account], preferredAccountId: account.Id);
        ServerRuleEditorViewModel? editor = null;
        vm.EditorRequested += e => editor = e;

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("Couldn't load client-side rules: rules.json is damaged and can't be read. "
                     + UnifiedRulesViewModel.ModeClause(false), vm.StatusText);

        vm.NewRuleCommand.Execute(null);
        editor!.Name = "New"; editor.SubjectContains = "y"; editor.MarkAsRead = true;
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.StartsWith("Couldn't save client-side rules:", editor.SaveError);
        Assert.Equal(Damaged, File.ReadAllText(_path));
    }

    [Fact]
    public void TheStatusBar_SaysTheRulesCantBeRead_NotThatThereAreNone()
    {
        var rules = new StubRuleService { ThrowOnLoad = new RulesFileUnreadableException(new IOException("locked")) };
        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), rules, new StubSmtpService());

        vm.UpdateRulesStatusText();

        Assert.Equal("Client-side rules can't be read — none are running", vm.RulesStatusText);
    }
}
