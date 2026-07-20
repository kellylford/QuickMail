using System;
using System.ComponentModel;
using QuickMail.Models;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for <see cref="CalendarEvent"/> property-change wiring.
/// </summary>
public class CalendarEventTests
{
    [Fact]
    public void ResponseStatusChange_RaisesDisplayLineChanged()
    {
        var evt = new CalendarEvent
        {
            Uid = "e1",
            Summary = "Team standup",
            StartTimeTicks = DateTime.Today.AddHours(10).ToUniversalTime().Ticks,
            ResponseStatus = CalendarResponseStatus.Pending,
        };

        var raisedProperties = new System.Collections.Generic.List<string?>();
        evt.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        evt.ResponseStatus = CalendarResponseStatus.Accepted;

        Assert.Contains(nameof(CalendarEvent.ResponseStatus), raisedProperties);
        Assert.Contains(nameof(CalendarEvent.DisplayLine), raisedProperties);
    }

    [Theory]
    // A harvested invite (real account, not graph) that is still Pending is the only pending invite.
    [InlineData(false, CalendarResponseStatus.Pending, true)]
    [InlineData(false, CalendarResponseStatus.Accepted, false)]
    [InlineData(false, CalendarResponseStatus.Declined, false)]
    [InlineData(false, CalendarResponseStatus.Tentative, false)]
    [InlineData(false, CalendarResponseStatus.Cancelled, false)]
    // Server-synced (graph) rows are never pending invites even at Pending status.
    [InlineData(true, CalendarResponseStatus.Pending, false)]
    public void IsPendingInvite_TrueOnlyForUnansweredHarvestedInvite(
        bool isGraph, CalendarResponseStatus status, bool expected)
    {
        var evt = new CalendarEvent
        {
            Uid = "e1",
            AccountId = Guid.NewGuid(),   // real (non-empty) account => not user-created
            IsGraph = isGraph,
            ResponseStatus = status,
        };

        Assert.Equal(expected, evt.IsPendingInvite);
    }

    [Fact]
    public void IsPendingInvite_FalseForUserCreatedAppointment()
    {
        var evt = new CalendarEvent
        {
            Uid = "local-1",
            AccountId = CalendarEvent.LocalAccountId,   // user-created
            ResponseStatus = CalendarResponseStatus.Pending,
        };

        Assert.False(evt.IsPendingInvite);
    }
}
