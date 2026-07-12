using System;
using System.Collections.Generic;
using System.Linq;
using QuickMail.Helpers;
using QuickMail.Models;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Covers <see cref="NewMailFilter"/> — the "genuinely new" rules that gate new-mail toasts:
/// unread only, arrived at/after the session threshold, and de-duplicated across repeated fires.
/// </summary>
public class NewMailFilterTests
{
    private static readonly Guid Acct = Guid.NewGuid();
    private static readonly DateTimeOffset Threshold = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);

    private static MailMessageSummary Msg(string id, DateTimeOffset date, bool read = false) => new()
    {
        MessageId = id,
        AccountId = Acct,
        From = "Jane <jane@example.com>",
        Subject = "Hello",
        Date = date,
        IsRead = read,
    };

    [Fact]
    public void FreshUnreadMessage_IsSelected()
    {
        var seen = new HashSet<string>();
        var result = NewMailFilter.SelectNew(
            new[] { Msg("1", Threshold.AddMinutes(5)) }, Threshold, seen);

        Assert.Single(result);
        Assert.Equal("1", result[0].MessageId);
    }

    [Fact]
    public void MessageBeforeThreshold_IsExcluded()
    {
        var seen = new HashSet<string>();
        var result = NewMailFilter.SelectNew(
            new[] { Msg("1", Threshold.AddMinutes(-1)) }, Threshold, seen);

        Assert.Empty(result);
    }

    [Fact]
    public void MessageAtThreshold_IsIncluded()
    {
        var seen = new HashSet<string>();
        var result = NewMailFilter.SelectNew(
            new[] { Msg("1", Threshold) }, Threshold, seen);

        Assert.Single(result);
    }

    [Fact]
    public void ReadMessage_IsExcluded()
    {
        var seen = new HashSet<string>();
        var result = NewMailFilter.SelectNew(
            new[] { Msg("1", Threshold.AddMinutes(5), read: true) }, Threshold, seen);

        Assert.Empty(result);
    }

    [Fact]
    public void RepeatedFire_NotifiesOnlyOnce()
    {
        var seen = new HashSet<string>();
        var m = Msg("1", Threshold.AddMinutes(5));

        var first  = NewMailFilter.SelectNew(new[] { m }, Threshold, seen);
        var second = NewMailFilter.SelectNew(new[] { m }, Threshold, seen);

        Assert.Single(first);
        Assert.Empty(second); // key already recorded — no duplicate toast
    }

    [Fact]
    public void MixedBatch_SelectsOnlyTheGenuinelyNew()
    {
        var seen = new HashSet<string>();
        var batch = new[]
        {
            Msg("old",   Threshold.AddMinutes(-10)),           // before launch
            Msg("read",  Threshold.AddMinutes(5), read: true), // already read
            Msg("new1",  Threshold.AddMinutes(5)),             // genuinely new
            Msg("new2",  Threshold.AddMinutes(6)),             // genuinely new
        };

        var result = NewMailFilter.SelectNew(batch, Threshold, seen);

        Assert.Equal(new[] { "new1", "new2" }, result.Select(m => m.MessageId).ToArray());
    }
}
