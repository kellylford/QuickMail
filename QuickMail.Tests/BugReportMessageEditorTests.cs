// The "Message editor" line in a bug report's Environment section (#1099). The new and previous
// message editors share almost no code, so a report about writing a message has to say which one
// was in use, and the setting alone does not: with it on, a compose window still falls back to
// the previous editor when the new one cannot start.

using System;
using System.Collections.Generic;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class BugReportMessageEditorTests
{
    private const string TurnedOff = "Previous (new editor turned off in Settings)";
    private const string FellBack = "Previous (the new editor could not start)";

    [Fact]
    public void Describe_SaysNew_WhenTheSettingIsOnAndNothingFellBack()
        => Assert.Equal("New", MessageEditorStatus.Describe(useNewEditor: true, fellBack: false));

    [Fact]
    public void Describe_SaysWhy_WhenTheNewEditorCouldNotStart()
        => Assert.Equal(FellBack, MessageEditorStatus.Describe(useNewEditor: true, fellBack: true));

    /// <summary>
    /// The setting wins: with it off the new editor is never tried, so an earlier fallback in the
    /// same run is not the reason the previous editor is showing.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Describe_SaysTurnedOff_WhenTheSettingIsOff(bool fellBack)
        => Assert.Equal(TurnedOff, MessageEditorStatus.Describe(useNewEditor: false, fellBack));

    // ── The wiring ────────────────────────────────────────────────────────────
    // BugReportServiceTests renders a hand-written context; these two show the captured context
    // carries the line and follows the setting, so removing the assignment cannot pass unnoticed.

    [Fact]
    public void CaptureBugReportContext_SaysPrevious_WhenTheSettingIsOff()
    {
        var vm = MakeViewModel(useNewEditor: false);

        Assert.Equal(TurnedOff, vm.CaptureBugReportContext().MessageEditor);
    }

    /// <summary>
    /// With the setting on the line is "New", unless some other test in this process made a
    /// compose window fall back, which is the one other thing it may truthfully say.
    /// </summary>
    [Fact]
    public void CaptureBugReportContext_FollowsTheSetting_WhenItIsOn()
    {
        var vm = MakeViewModel(useNewEditor: true);

        Assert.Contains(vm.CaptureBugReportContext().MessageEditor, new[] { "New", FellBack });
    }

    private sealed class NoAccounts : IAccountService
    {
        public List<AccountModel> LoadAccounts() => [];
        public void SaveAccounts(List<AccountModel> a) { }
        public void SetDefaultAccount(Guid accountId) { }
    }

    private static MainViewModel MakeViewModel(bool useNewEditor)
    {
        var config = new StubConfigService();
        config.Save(new ConfigModel { UseNewEditor = useNewEditor });
        return new MainViewModel(
            new StubImapMailService(), new NoAccounts(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
            config, new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService());
    }
}
