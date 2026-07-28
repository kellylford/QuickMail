using System;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Issue #396: "sending an email gives no feedback, and does not close the compose window."
///
/// The window staying open was the visible half; the silence was the half that made it
/// undiagnosable. Every message the send path produces is announced by the compose window under the
/// category the ViewModel assigns, and an outcome announced as Status is silent for anyone who has
/// turned background-progress announcements off — which is a setting QuickMail offers and expects
/// people to use. These tests pin the category, because it is invisible in the running app: the
/// status bar shows the text either way, so this can only regress silently.
/// </summary>
public class ComposeViewModelSendFeedbackTests
{
    private static (ComposeViewModel vm, StubSmtpService smtp) MakeVm()
    {
        var smtp = new StubSmtpService();
        var vm = new ComposeViewModel(
            smtp,
            new StubAccountService(),
            new StubCredentialService(),
            new RecordingMailService(),
            new StubTemplateService());
        return (vm, smtp);
    }

    /// <summary>An ordinary, sendable account: OAuth so no stored password is needed.</summary>
    private static AccountModel Account(string username = "samuel@interfree.ca") => new()
    {
        Id = Guid.NewGuid(),
        Username = username,
        AuthType = AuthType.OAuth2Google,
    };

    [Fact]
    public async Task SendFailure_IsAnnouncedAsAResultNotAsProgress()
    {
        var (vm, smtp) = MakeVm();
        vm.SenderAccount = Account();
        vm.To = "someone@example.com";
        smtp.SendFailure = new InvalidOperationException("550 sender rejected");

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Contains("Send failed", vm.StatusText);
        Assert.Contains("550 sender rejected", vm.StatusText);
        Assert.Equal(AnnouncementCategory.Result, vm.StatusCategory);
    }

    [Fact]
    public async Task SendFailure_LeavesTheWindowOpenAndTheMessageUnsent()
    {
        var (vm, smtp) = MakeVm();
        vm.SenderAccount = Account();
        vm.To = "someone@example.com";
        smtp.SendFailure = new InvalidOperationException("nope");
        var closed = false;
        vm.CloseRequested += () => closed = true;

        await vm.SendCommand.ExecuteAsync(null);

        Assert.False(closed);
        Assert.False(vm.IsSent);
        Assert.False(vm.IsBusy);   // the Send button comes back, which is what the reporter saw
    }

    [Fact]
    public async Task SuccessfulSend_AnnouncesTheOutcomeAndClosesTheWindow()
    {
        var (vm, _) = MakeVm();
        vm.SenderAccount = Account();
        vm.To = "someone@example.com";
        var closed = false;
        vm.CloseRequested += () => closed = true;

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal("Message sent.", vm.StatusText);
        Assert.Equal(AnnouncementCategory.Result, vm.StatusCategory);
        Assert.True(vm.IsSent);
        Assert.True(closed);
    }

    [Fact]
    public async Task RefusedSend_AnnouncesWhyAsAResult()
    {
        var (vm, smtp) = MakeVm();
        vm.SenderAccount = Account();
        vm.To = "   ";   // nothing to send to

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal("Please enter at least one recipient.", vm.StatusText);
        Assert.Equal(AnnouncementCategory.Result, vm.StatusCategory);
        Assert.Empty(smtp.Sent);
    }

    /// <summary>
    /// The reporter's account had a login name where the email address belongs, so the From header —
    /// and the SMTP envelope sender — was a bare "fastfinge" with no domain. The server's rejection
    /// of that says nothing about which QuickMail field is wrong, so the send is refused here with a
    /// message that names the field and where to fix it.
    /// </summary>
    [Fact]
    public async Task SenderAddressWithNoDomain_IsRefusedBeforeTheSendWithAnActionableMessage()
    {
        var (vm, smtp) = MakeVm();
        vm.SenderAccount = Account(username: "fastfinge");
        vm.To = "someone@example.com";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Empty(smtp.Sent);
        Assert.False(vm.IsSent);
        Assert.Contains("not a valid email address", vm.StatusText);
        Assert.Contains("Manage Accounts", vm.StatusText);
        Assert.Equal(AnnouncementCategory.Result, vm.StatusCategory);
    }

    [Fact]
    public async Task ProgressWhileSavingADraft_StaysStatusButTheOutcomeIsAResult()
    {
        var (vm, _) = MakeVm();
        vm.SenderAccount = Account();
        vm.Subject = "something to save";

        await vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.Equal("Draft saved.", vm.StatusText);
        Assert.Equal(AnnouncementCategory.Result, vm.StatusCategory);
    }

    [Fact]
    public async Task DraftSaveFailure_IsAnnouncedAsAResult()
    {
        var imap = new RecordingMailService { AppendDraftThrows = true };
        var vm = new ComposeViewModel(
            new StubSmtpService(), new StubAccountService(), new StubCredentialService(),
            imap, new StubTemplateService());
        vm.SenderAccount = Account();
        vm.Subject = "something to save";

        await vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.Contains("Save draft failed", vm.StatusText);
        Assert.Equal(AnnouncementCategory.Result, vm.StatusCategory);
    }
}
