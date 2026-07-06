using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>In-memory credential store so tests can observe/seed what SubmitAsync reads and writes.</summary>
sealed class FakeCredentialService : ICredentialService
{
    private readonly Dictionary<string, string> _secrets = new();

    public void SavePassword(Guid accountId, string password) { }
    public string? GetPassword(Guid accountId) => null;
    public void DeletePassword(Guid accountId) { }

    public void SaveSecret(string key, string value) => _secrets[key] = value;
    public string? GetSecret(string key) => _secrets.TryGetValue(key, out var v) ? v : null;
    public void DeleteSecret(string key) => _secrets.Remove(key);
}

/// <summary>Routes requests to a caller-supplied responder instead of the network.</summary>
sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return _responder(request);
    }
}

public class BugReportServiceTests
{
    private static BugReportModel SampleReport(string summary = "Something broke") => new()
    {
        Summary = summary,
        WhatHappened = "It broke when I clicked the button.",
        WhatExpected = "It should not break.",
        StepsToReproduce = "1. Click button\n2. Observe crash",
    };

    private static BugReportService MakeService(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        out FakeHttpMessageHandler handler,
        FakeCredentialService? credentials = null,
        string appOwnedToken = "")   // hermetic: never inherit the CI-baked compiled-in token
    {
        handler = new FakeHttpMessageHandler(responder);
        return new BugReportService(credentials ?? new FakeCredentialService(), handler, appOwnedToken);
    }

    [Fact]
    public async Task SubmitAsync_NoTokenAvailable_FailsWithoutHttpCall()
    {
        var handlerCalled = false;
        var service = MakeService(_ => { handlerCalled = true; return new HttpResponseMessage(HttpStatusCode.OK); }, out _);

        var result = await service.SubmitAsync(SampleReport());

        Assert.False(result.Success);
        Assert.False(handlerCalled);
        Assert.Null(result.IssueUrl);
    }

    [Fact]
    public async Task SubmitAsync_UsesAppOwnedToken_WhenNoStoredSecret()
    {
        // No per-user stored secret; only the app-owned token is available (the normal
        // release path). Submit must use it and cache it into the credential store.
        var credentials = new FakeCredentialService();
        var service = MakeService(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("{\"html_url\":\"https://github.com/kellylford/QuickMail/issues/1\"}"),
        }, out var handler, credentials, appOwnedToken: "app-token");

        var result = await service.SubmitAsync(SampleReport());

        Assert.True(result.Success);
        Assert.Equal("app-token", handler.LastRequest!.Headers.Authorization!.Parameter);
        Assert.Equal("app-token", credentials.GetSecret("QuickMail.BugReportService.AppOwnedToken"));
    }

    [Fact]
    public async Task SubmitAsync_Success_ReturnsIssueUrl()
    {
        var credentials = new FakeCredentialService();
        credentials.SaveSecret("QuickMail.BugReportService.AppOwnedToken", "fake-token");

        var service = MakeService(req => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("{\"html_url\":\"https://github.com/kellylford/QuickMail/issues/999\"}"),
        }, out var handler, credentials);

        var result = await service.SubmitAsync(SampleReport());

        Assert.True(result.Success);
        Assert.Equal("https://github.com/kellylford/QuickMail/issues/999", result.IssueUrl);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("fake-token", handler.LastRequest!.Headers.Authorization!.Parameter);
        Assert.Contains("user-reported", handler.LastRequestBody);
        Assert.DoesNotContain("quickmail.log", handler.LastRequestBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitAsync_RevokedToken_FailsGracefully()
    {
        var credentials = new FakeCredentialService();
        credentials.SaveSecret("QuickMail.BugReportService.AppOwnedToken", "bad-token");

        var service = MakeService(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized), out _, credentials);

        var result = await service.SubmitAsync(SampleReport());

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task SubmitAsync_NetworkFailure_FailsGracefully()
    {
        var credentials = new FakeCredentialService();
        credentials.SaveSecret("QuickMail.BugReportService.AppOwnedToken", "fake-token");

        var service = MakeService(_ => throw new HttpRequestException("offline"), out _, credentials);

        var result = await service.SubmitAsync(SampleReport());

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void BuildFallbackUrl_EncodesSpecialCharacters()
    {
        var service = MakeService(_ => new HttpResponseMessage(HttpStatusCode.OK), out _);
        var report = SampleReport("Crash with \"quotes\" & ampersands\nand newlines");

        var url = service.BuildFallbackUrl(report);

        Assert.StartsWith("https://github.com/kellylford/QuickMail/issues/new?", url);
        Assert.DoesNotContain("\"", url);
        Assert.DoesNotContain("\n", url);
    }

    [Fact]
    public void BuildFallbackUrl_VeryLongReport_TruncatesBodyInsteadOfProducingAnUnusableUrl()
    {
        var service = MakeService(_ => new HttpResponseMessage(HttpStatusCode.OK), out _);
        var report = SampleReport();
        report.WhatHappened = new string('x', 10_000);

        var url = service.BuildFallbackUrl(report);

        // The encoded body must stay well short of what could fail via ShellExecute, and the
        // truncated content should not just be silently cut — it should point the user back to
        // the clipboard copy, which always has the full untruncated text.
        Assert.True(url.Length < 6000, $"Fallback URL was {url.Length} chars — expected truncation to keep it well under browser/shell limits.");
        Assert.Contains(Uri.EscapeDataString("truncated"), url);
    }

    [Fact]
    public void BuildReportText_ContainsOnlyUserTextAndMetadata_NoLogContent()
    {
        var service = MakeService(_ => new HttpResponseMessage(HttpStatusCode.OK), out _);
        var report = SampleReport();

        var text = service.BuildReportText(report);

        Assert.Contains(report.WhatHappened, text);
        Assert.Contains(report.WhatExpected, text);
        Assert.Contains(report.StepsToReproduce, text);
        Assert.Contains("QuickMail version", text);
        Assert.Contains("OS:", text);
        Assert.DoesNotContain("quickmail.log", text, StringComparison.OrdinalIgnoreCase);
    }
}
