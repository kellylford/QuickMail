using System;
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
