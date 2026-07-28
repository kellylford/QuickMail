using System;
using System.Linq;
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
/// people to use.
///
/// These tests capture the category the way the View does, through
/// <see cref="StatusAnnouncementRecorder"/>: inside the PropertyChanged notification. Reading it
/// afterwards would pass against a broken implementation, since the category is one-shot.
/// </summary>
public class ComposeViewModelSendFeedbackTests
{
    private static (ComposeViewModel vm, StubSmtpService smtp, StatusAnnouncementRecorder status) MakeVm()
    {
        var smtp = new StubSmtpService();
        var vm = new ComposeViewModel(
            smtp,
            new StubAccountService(),
            new StubCredentialService(),
            new RecordingMailService(),
            new StubTemplateService());
        return (vm, smtp, StatusAnnouncementRecorder.Watch(vm));
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
        var (vm, smtp, status) = MakeVm();
        vm.SenderAccount = Account();
        vm.To = "someone@example.com";
        smtp.SendFailure = new InvalidOperationException("550 sender rejected");

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Contains("Send failed", vm.StatusText);
        Assert.Contains("550 sender rejected", vm.StatusText);
        Assert.Equal(AnnouncementCategory.Result, status.Last.Category);
    }

    /// <summary>
    /// The progress message and the outcome are different categories, in that order. Asserting the
    /// whole sequence is what stops "Sending…" from quietly becoming an interrupting Result.
    /// </summary>
    [Fact]
    public async Task SendingAnnouncesProgressThenTheOutcome()
    {
        var (vm, smtp, status) = MakeVm();
        vm.SenderAccount = Account();
        vm.To = "someone@example.com";
        smtp.SendFailure = new InvalidOperationException("nope");

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(2, status.Announced.Count);
        Assert.Equal(("Sending…", AnnouncementCategory.Status), status.Announced[0]);
        Assert.Equal(AnnouncementCategory.Result, status.Announced[1].Category);
    }

    /// <summary>
    /// The category must not stay latched after an outcome. It used to, which meant one refusal
    /// re-classified every later background message as an interrupting Result — the inverse of the
    /// bug this change exists to fix.
    /// </summary>
    [Fact]
    public async Task AnOutcomeDoesNotLeaveLaterProgressClassifiedAsAnOutcome()
    {
        var (vm, smtp, status) = MakeVm();
        vm.SenderAccount = Account();

        vm.To = string.Empty;
        await vm.SendCommand.ExecuteAsync(null);       // a refusal: Result
        Assert.Equal(AnnouncementCategory.Result, status.Last.Category);

        vm.To = "someone@example.com";
        smtp.SendFailure = new InvalidOperationException("nope");
        await vm.SendCommand.ExecuteAsync(null);       // "Sending…" must be Status again

        Assert.Contains(status.Announced, a => a.Text == "Sending…" && a.Category == AnnouncementCategory.Status);
    }

    /// <summary>
    /// Pressing the button twice with the same field still wrong must speak twice. StatusText is an
    /// [ObservableProperty] with an equality check, so the second press used to raise no
    /// notification at all — the user presses a button and hears nothing, which is the reported
    /// symptom.
    /// </summary>
    [Fact]
    public async Task RepeatingTheSameRefusalAnnouncesItAgain()
    {
        var (vm, _, status) = MakeVm();
        vm.SenderAccount = Account();
        vm.To = string.Empty;

        await vm.SendCommand.ExecuteAsync(null);
        await vm.SendCommand.ExecuteAsync(null);

        var refusals = status.Announced.Where(a => a.Text == "Please enter at least one recipient.").ToList();
        Assert.Equal(2, refusals.Count);
        Assert.All(refusals, r => Assert.Equal(AnnouncementCategory.Result, r.Category));
    }

    [Fact]
    public async Task SendFailure_LeavesTheWindowOpenAndTheMessageUnsent()
    {
        var (vm, smtp, _) = MakeVm();
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
        var (vm, _, status) = MakeVm();
        vm.SenderAccount = Account();
        vm.To = "someone@example.com";
        var closed = false;
        vm.CloseRequested += () => closed = true;

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(("Message sent.", AnnouncementCategory.Result), status.Last);
        Assert.True(vm.IsSent);
        Assert.True(closed);
    }

    [Fact]
    public async Task RefusedSend_AnnouncesWhyAsAResult()
    {
        var (vm, smtp, status) = MakeVm();
        vm.SenderAccount = Account();
        vm.To = "   ";   // nothing to send to

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(("Please enter at least one recipient.", AnnouncementCategory.Result), status.Last);
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
        var (vm, smtp, status) = MakeVm();
        vm.SenderAccount = Account(username: "fastfinge");
        vm.To = "someone@example.com";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Empty(smtp.Sent);
        Assert.False(vm.IsSent);
        Assert.Contains("not a valid email address", vm.StatusText);
        Assert.Contains("Manage Accounts", vm.StatusText);
        Assert.Equal(AnnouncementCategory.Result, status.Last.Category);
    }

    /// <summary>
    /// A sender address the validator accepts must be one MimeMessageBuilder can actually build a
    /// From header from. These are the forms that parse as a mailbox but throw in the
    /// MailboxAddress(name, address) constructor — if the guard let them through, the user would get
    /// "Send failed: Invalid addr-spec token at offset 0", which is exactly the unactionable error
    /// this issue is about.
    /// </summary>
    [Theory]
    [InlineData("Kelly Ford <kelly@example.com>")]
    [InlineData("<kelly@example.com>")]
    [InlineData("  kelly@example.com  ")]
    public async Task ASenderAddressTheGuardAcceptsCanAlwaysBuildAFromHeader(string username)
    {
        var (vm, smtp, _) = MakeVm();
        vm.SenderAccount = Account(username);
        vm.To = "someone@example.com";

        await vm.SendCommand.ExecuteAsync(null);

        // Either refused up front, or sent — never an exception surfacing as "Send failed".
        Assert.DoesNotContain("addr-spec", vm.StatusText);
        Assert.DoesNotContain("Send failed", vm.StatusText);
        if (vm.IsSent) Assert.Single(smtp.Sent);
    }

    [Fact]
    public async Task SavingADraftAnnouncesProgressThenTheOutcome()
    {
        var (vm, _, status) = MakeVm();
        vm.SenderAccount = Account();
        vm.Subject = "something to save";

        await vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.Equal(2, status.Announced.Count);
        Assert.Equal(("Saving draft…", AnnouncementCategory.Status), status.Announced[0]);
        Assert.Equal(("Draft saved.", AnnouncementCategory.Result), status.Announced[1]);
    }

    [Fact]
    public async Task DraftSaveFailure_IsAnnouncedAsAResult()
    {
        var imap = new RecordingMailService { AppendDraftThrows = true };
        var vm = new ComposeViewModel(
            new StubSmtpService(), new StubAccountService(), new StubCredentialService(),
            imap, new StubTemplateService());
        var status = StatusAnnouncementRecorder.Watch(vm);
        vm.SenderAccount = Account();
        vm.Subject = "something to save";

        await vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.Contains("Save draft failed", vm.StatusText);
        Assert.Equal(AnnouncementCategory.Result, status.Last.Category);
    }
}
