// The "Message editor" line in a bug report's Environment section (#1099). The new and previous
// message editors share almost no code, so a report about writing a message has to say which one
// was in use. The setting alone does not say: a message window chooses its editor when it opens,
// and with the setting on it still falls back to the previous editor when the new one cannot
// start, or for HTML format alone when one message will not load. The line reports what the
// message windows of this run recorded.
//
// Not covered here: that each fallback site in ComposeWindow records itself. That needs WinUI to
// fail inside a real window, which this test process cannot arrange.

using System;
using System.Collections.Generic;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;
using State = QuickMail.Helpers.MessageEditorStatus.State;

namespace QuickMail.Tests;

public class BugReportMessageEditorTests
{
    private const string TurnedOff = "Previous (the new editor is turned off in Settings)";

    private static State Opened(bool settingOn, bool usesNew, int htmlFallbacks = 0)
        => new(AnyMessageOpened: true, LastSettingOn: settingOn, LastUsesNewEditor: usesNew, HtmlFallbacks: htmlFallbacks);

    [Fact]
    public void SaysNew_WhenTheLastMessageWindowUsedTheNewEditor()
        => Assert.Equal("New", MessageEditorStatus.Describe(settingNow: true, Opened(settingOn: true, usesNew: true)));

    [Fact]
    public void SaysTurnedOff_WhenTheLastMessageWindowOpenedWithTheSettingOff()
        => Assert.Equal(TurnedOff, MessageEditorStatus.Describe(settingNow: false, Opened(settingOn: false, usesNew: false)));

    [Fact]
    public void SaysCouldNotStart_WhenTheSettingWasOnButTheWindowGotThePreviousEditor()
        => Assert.Equal("Previous (the new editor could not start)",
                        MessageEditorStatus.Describe(settingNow: true, Opened(settingOn: true, usesNew: false)));

    /// <summary>
    /// The case the first version of this got backwards: trouble in the new editor, the setting
    /// turned off to get past it, then the report. The line must still say the new editor.
    /// </summary>
    [Fact]
    public void StillSaysNew_WhenTheSettingWasTurnedOffAfterTheMessageOpened()
        => Assert.Equal("New; turned off in Settings since that message opened",
                        MessageEditorStatus.Describe(settingNow: false, Opened(settingOn: true, usesNew: true)));

    [Fact]
    public void StillSaysPrevious_WhenTheSettingWasTurnedOnAfterTheMessageOpened()
        => Assert.Equal(TurnedOff + "; turned on in Settings since that message opened",
                        MessageEditorStatus.Describe(settingNow: true, Opened(settingOn: false, usesNew: false)));

    /// <summary>
    /// One message whose HTML would not load sends HTML format alone to the previous editor. That
    /// is a count beside "New", never a reason to call the whole editor "Previous".
    /// </summary>
    [Theory]
    [InlineData(1, "New; HTML format went back to the previous editor 1 time this session")]
    [InlineData(3, "New; HTML format went back to the previous editor 3 times this session")]
    public void CountsHtmlFallbacks_WithoutCallingTheEditorPrevious(int count, string expected)
        => Assert.Equal(expected, MessageEditorStatus.Describe(settingNow: true, Opened(settingOn: true, usesNew: true, count)));

    [Theory]
    [InlineData(true, "New; no message window opened this session")]
    [InlineData(false, TurnedOff + "; no message window opened this session")]
    public void GoesByTheSetting_WhenNoMessageWindowHasOpened(bool settingNow, string expected)
        => Assert.Equal(expected, MessageEditorStatus.Describe(settingNow, default));

    // ── The wiring ────────────────────────────────────────────────────────────
    // BugReportServiceTests renders a hand-written context. This shows the captured context asks
    // for the line with the setting as it is now and carries the answer, so removing the
    // assignment cannot pass unnoticed. The seam keeps it clear of what other tests' message
    // windows recorded in the shared process.

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CaptureBugReportContext_CarriesTheLine_ForTheCurrentSetting(bool useNewEditor)
    {
        var vm = MakeViewModel(useNewEditor);
        bool? asked = null;
        vm.DescribeMessageEditor = setting => { asked = setting; return "the line"; };

        Assert.Equal("the line", vm.CaptureBugReportContext().MessageEditor);
        Assert.Equal(useNewEditor, asked);
    }

    /// <summary>Left alone, the view model reports from the real record, not a blank.</summary>
    [Fact]
    public void CaptureBugReportContext_UsesTheRealRecordByDefault()
    {
        var line = MakeViewModel(useNewEditor: true).CaptureBugReportContext().MessageEditor;

        Assert.True(line.StartsWith("New", StringComparison.Ordinal) || line.StartsWith("Previous", StringComparison.Ordinal), line);
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
