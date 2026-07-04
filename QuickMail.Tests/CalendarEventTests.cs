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
}
