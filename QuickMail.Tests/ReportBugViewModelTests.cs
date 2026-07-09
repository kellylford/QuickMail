using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

sealed class FakeBugReportService : IBugReportService
{
    public BugReportResult NextResult = BugReportResult.Succeeded("https://github.com/kellylford/QuickMail/issues/1");
    public BugReportModel? LastSubmitted;

    public Task<BugReportResult> SubmitAsync(BugReportModel report, CancellationToken cancellationToken = default)
    {
        LastSubmitted = report;
        return Task.FromResult(NextResult);
    }

    public string BuildFallbackUrl(BugReportModel report) =>
        $"https://github.com/kellylford/QuickMail/issues/new?title={report.Summary}";

    public string BuildReportText(BugReportModel report) => $"BODY:{report.WhatHappened}";
}

// WpfTests collection: serialize with the other WPF/STA tests so no two STA window-owning
// threads run concurrently (issue #211).
[Collection("WpfTests")]
public class ReportBugViewModelTests
{
    private static ReportBugViewModel Make(out FakeBugReportService service)
    {
        service = new FakeBugReportService();
        return new ReportBugViewModel(service);
    }

    [Fact]
    public async Task Send_BlankSummary_DoesNotCallServiceAndAnnouncesValidationError()
    {
        var vm = Make(out var service);
        string? announced = null;
        vm.AnnouncementRequested += (text, _) => announced = text;

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Null(service.LastSubmitted);
        Assert.Equal("Enter a summary before sending.", announced);
    }

    [Fact]
    public async Task Send_Success_SetsIssueUrlAndRaisesSendSucceeded()
    {
        var vm = Make(out var service);
        vm.Summary = "Something broke";
        vm.WhatHappened = "It broke.";
        service.NextResult = BugReportResult.Succeeded("https://github.com/kellylford/QuickMail/issues/42");

        var succeeded = false;
        vm.SendSucceeded += (_, _) => succeeded = true;

        await vm.SendCommand.ExecuteAsync(null);

        Assert.True(vm.IsSent);
        Assert.False(vm.IsSending);
        Assert.Equal("https://github.com/kellylford/QuickMail/issues/42", vm.IssueUrl);
        Assert.True(succeeded);
        Assert.Equal("Close", vm.CancelButtonLabel);
    }

    [Fact]
    public async Task Send_Failure_ReEnablesSendAndRaisesSendFailed()
    {
        var vm = Make(out var service);
        vm.Summary = "Something broke";
        vm.WhatHappened = "It broke.";
        service.NextResult = BugReportResult.Failed("no token");

        var failed = false;
        vm.SendFailed += (_, _) => failed = true;

        await vm.SendCommand.ExecuteAsync(null);

        Assert.False(vm.IsSent);
        Assert.False(vm.IsSending);
        Assert.True(vm.CanSend); // Send is re-enabled, not stuck disabled
        Assert.True(failed);
        Assert.Equal("Cancel", vm.CancelButtonLabel);
    }

    [Fact]
    public void PreviewText_ReflectsCurrentFieldValues_NoLogContent()
    {
        var vm = Make(out _);
        vm.WhatHappened = "the crash details";

        Assert.Contains("the crash details", vm.PreviewText);
        Assert.DoesNotContain("quickmail.log", vm.PreviewText);
    }

    // Note: CopyAndOpenCommand with a non-blank summary is intentionally not exercised here —
    // it calls ExternalUriPolicy.TryOpenExternal, which would launch a real browser during the
    // test run (see ExternalUriPolicyTests, which for the same reason only tests blocked
    // schemes, never an allowed one). The blank-summary guard below is safe to test because it
    // returns before either the clipboard or ExternalUriPolicy is touched.
    [StaFact]
    public void CopyAndOpen_BlankSummary_DoesNotCopyOrOpen()
    {
        var vm = Make(out var service);
        Clipboard.SetText("unchanged");

        vm.CopyAndOpenCommand.Execute(null);

        Assert.Equal("unchanged", Clipboard.GetText());
    }

    [Fact]
    public void Cancel_RaisesCloseRequested()
    {
        var vm = Make(out _);
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.CancelCommand.Execute(null);

        Assert.True(closed);
    }
}
