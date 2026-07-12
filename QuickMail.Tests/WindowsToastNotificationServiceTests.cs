using System;
using Microsoft.Toolkit.Uwp.Notifications;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Smoke coverage for <see cref="WindowsToastNotificationService"/>. A headless test can't assert
/// that a toast actually renders, so these verify the guard rails: construction and the empty-set
/// path never throw, and <see cref="INotificationService.IsSupported"/> is queryable. The tests
/// deliberately do not fire a real toast so the suite stays free of on-screen side effects.
/// </summary>
public class WindowsToastNotificationServiceTests
{
    [Fact]
    public void Construction_AndIsSupported_DoNotThrow()
    {
        using var svc = new WindowsToastNotificationService();
        _ = svc.IsSupported; // returns a bool without throwing
    }

    [Fact]
    public void ShowNewMail_WithEmptySet_IsANoOp()
    {
        using var svc = new WindowsToastNotificationService();
        svc.ShowNewMail("Account", Guid.NewGuid(), Array.Empty<MailMessageSummary>());
        // No exception = pass. An empty set must never attempt to build or show a toast.
    }

    [Fact]
    public void ParseActivation_ExtractsAccountFolderAndMessage()
    {
        var accountId = Guid.NewGuid();
        var arg = new ToastArguments()
            .Add("action", "openMail")
            .Add("accountId", accountId.ToString())
            .Add("folder", "INBOX")
            .Add("messageId", "42")
            .ToString();

        var act = WindowsToastNotificationService.ParseActivation(arg);

        Assert.NotNull(act);
        Assert.Equal(accountId, act!.AccountId);
        Assert.Equal("INBOX", act.Folder);
        Assert.Equal("42", act.MessageId);
    }

    [Fact]
    public void ParseActivation_MultiMessageToast_HasNoMessageId()
    {
        var accountId = Guid.NewGuid();
        var arg = new ToastArguments()
            .Add("action", "openMail")
            .Add("accountId", accountId.ToString())
            .Add("folder", "INBOX")
            .ToString();

        var act = WindowsToastNotificationService.ParseActivation(arg);

        Assert.NotNull(act);
        Assert.Equal(accountId, act!.AccountId);
        Assert.Null(act.MessageId); // no single target -> handler just foregrounds
    }

    [Fact]
    public void ParseActivation_InfoToast_ReturnsNull()
    {
        var arg = new ToastArguments().Add("action", "info").ToString();
        Assert.Null(WindowsToastNotificationService.ParseActivation(arg));
    }

    [Fact]
    public void DispatchActivation_BuffersUntilFirstSubscriber_ThenReplays()
    {
        using var svc = new WindowsToastNotificationService();
        var act = new NotificationActivation(Guid.NewGuid(), "INBOX", "7");

        // Arrives before anyone subscribes (cold start) — must not be dropped.
        svc.DispatchActivation(act);

        NotificationActivation? received = null;
        svc.Activated += a => received = a; // first subscriber gets the buffered activation

        Assert.Same(act, received);
    }

    [Fact]
    public void DispatchActivation_WithSubscriber_DeliversImmediately_AndOnce()
    {
        using var svc = new WindowsToastNotificationService();
        var count = 0;
        svc.Activated += _ => count++;

        svc.DispatchActivation(new NotificationActivation(Guid.NewGuid(), "INBOX", "1"));
        Assert.Equal(1, count);

        // A late second subscriber must NOT receive the already-delivered activation.
        NotificationActivation? late = null;
        svc.Activated += a => late = a;
        Assert.Null(late);
    }

    [Theory]
    [InlineData("Jane Smith <jane@example.com>", "Jane Smith")]   // name + address → name only
    [InlineData("\"Smith, Jane\" <jane@example.com>", "Smith, Jane")] // quoted name preserved
    [InlineData("<jane@example.com>", "jane@example.com")]         // no display name → bare address
    [InlineData("jane@example.com", "jane@example.com")]           // bare address
    [InlineData("", "")]                                          // empty stays empty
    public void SenderDisplayName_StripsAddress(string from, string expected)
    {
        Assert.Equal(expected, WindowsToastNotificationService.SenderDisplayName(from));
    }
}
