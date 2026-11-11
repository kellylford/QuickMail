using System;
using System.IO;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class ComposeSendConfirmationTests
{
    private sealed class Harness
    {
        public StubSmtpService Smtp { get; } = new();
        public StubOutboxService Outbox { get; } = new();
        public StubConnectivityService Connectivity { get; } = new();
        public ComposeViewModel Vm { get; }

        public Harness()
        {
            Vm = new ComposeViewModel(Smtp, new StubAccountService(), new StubCredentialService(),
                new StubImapMailService(), new StubTemplateService(), outbox: Outbox, connectivity: Connectivity)
            {
                SenderAccount = new AccountModel { Id = Guid.NewGuid(), Username = "sender@example.com", AuthType = AuthType.OAuth2Google },
                To = "recipient@example.com",
                Body = "Still writing",
                ConfirmBeforeSend = true,
            };
        }
    }

    [Theory]
    [InlineData(ComposeMode.PlainText)]
    [InlineData(ComposeMode.Markdown)]
    [InlineData(ComposeMode.Html)]
    public async Task Decline_KeepsMessageAndDoesNotSendOrQueue(ComposeMode mode)
    {
        var h = new Harness();
        h.Vm.SetMode(mode);
        h.Vm.ConfirmationRequested = (_, title) => { Assert.Equal("Confirm Send", title); return false; };
        var closes = 0;
        h.Vm.CloseRequested += () => closes++;

        await h.Vm.SendCommand.ExecuteAsync(null);

        Assert.Empty(h.Smtp.Sent);
        Assert.Empty(h.Outbox.Enqueued);
        Assert.False(h.Vm.IsSent);
        Assert.True(h.Vm.IsDirty);
        Assert.False(h.Vm.IsBusy);
        Assert.Equal("Still writing", h.Vm.Body);
        Assert.Equal(0, closes);
    }

    [Fact]
    public async Task MissingConfirmationHandler_DoesNotSend()
    {
        var h = new Harness();
        await h.Vm.SendCommand.ExecuteAsync(null);
        Assert.Empty(h.Smtp.Sent);
        Assert.False(h.Vm.IsBusy);
    }

    [Fact]
    public async Task ReentrantSendDuringConfirmation_ConfirmsAndSendsOnce()
    {
        var h = new Harness();
        var confirmations = 0;
        h.Vm.ConfirmationRequested = (_, _) =>
        {
            confirmations++;
            Assert.True(h.Vm.IsBusy);
            Assert.True(h.Vm.SendCommand.ExecuteAsync(null).IsCompletedSuccessfully);
            return true;
        };

        await h.Vm.SendCommand.ExecuteAsync(null);
        await h.Vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(1, confirmations);
        Assert.Single(h.Smtp.Sent);
        Assert.True(h.Vm.IsSent);
    }

    [Fact]
    public async Task RepeatedSendDuringNetworkWait_AndAfterSuccess_SendsOnce()
    {
        var h = new Harness();
        h.Vm.ConfirmBeforeSend = false;
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Smtp.SendWait = () => finish.Task;

        var first = h.Vm.SendCommand.ExecuteAsync(null);
        try
        {
            await h.Vm.SendCommand.ExecuteAsync(null);
            Assert.Single(h.Smtp.Sent);
        }
        finally { finish.SetResult(); }
        await first;
        await h.Vm.SendCommand.ExecuteAsync(null);
        Assert.Single(h.Smtp.Sent);
    }

    [Fact]
    public async Task OfflineSend_DeclineDoesNotQueue_ConfirmQueuesOnlyOnce()
    {
        var h = new Harness();
        h.Connectivity.SetAccount(h.Vm.SenderAccount!.Id, false);
        h.Vm.ConfirmationRequested = (_, _) => false;
        await h.Vm.SendCommand.ExecuteAsync(null);
        Assert.Empty(h.Outbox.Enqueued);

        h.Vm.ConfirmationRequested = (_, _) => true;
        await h.Vm.SendCommand.ExecuteAsync(null);
        await h.Vm.SendCommand.ExecuteAsync(null);
        Assert.Single(h.Outbox.Enqueued);
        Assert.Empty(h.Smtp.Sent);
        Assert.True(h.Vm.IsSent);
    }

    [Fact]
    public async Task FailedSend_CanBeRetriedWithFreshConfirmation()
    {
        var h = new Harness();
        var confirmations = 0;
        h.Vm.ConfirmationRequested = (_, _) => { confirmations++; return true; };
        h.Smtp.SendFailure = new InvalidOperationException("Rejected");
        await h.Vm.SendCommand.ExecuteAsync(null);
        Assert.False(h.Vm.IsSent);
        h.Smtp.SendFailure = null;
        await h.Vm.SendCommand.ExecuteAsync(null);
        Assert.Equal(2, confirmations);
        Assert.Single(h.Smtp.Sent);
    }

    [Fact]
    public async Task BusyDraftSave_AndInvalidMessage_DoNotPromptOrSend()
    {
        var h = new Harness();
        h.Vm.ConfirmationRequested = (_, _) => throw new InvalidOperationException("Must not prompt");
        h.Vm.IsBusy = true;
        await h.Vm.SendCommand.ExecuteAsync(null);
        h.Vm.IsBusy = false;
        h.Vm.To = "";
        await h.Vm.SendCommand.ExecuteAsync(null);
        Assert.Empty(h.Smtp.Sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Preference_SurvivesSettingsSaveAndRestart(bool enabled)
    {
        var directory = Path.Combine(Path.GetTempPath(), "qm-confirm-" + Guid.NewGuid().ToString("N"));
        try
        {
            var profile = new ProfileContext(directory);
            var config = new ConfigService(profile);
            Assert.False(config.Load().ConfirmBeforeSend);
            var settings = new SettingsViewModel(config, new StubCommandRegistry()) { ConfirmBeforeSend = enabled };
            settings.SaveCommand.Execute(null);
            var reloaded = new ConfigService(profile);
            Assert.Equal(enabled, reloaded.Load().ConfirmBeforeSend);
            Assert.Equal(enabled, new SettingsViewModel(reloaded, new StubCommandRegistry()).ConfirmBeforeSend);
            Assert.Contains(nameof(ConfigModel.ConfirmBeforeSend), SettingsConfigTransfer.Portable);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
