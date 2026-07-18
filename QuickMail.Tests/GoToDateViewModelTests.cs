using System;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for <see cref="GoToDateViewModel"/> — the Go To Date picker's authoring VM.
/// Pure VM logic, no UI or STA thread.
/// </summary>
public class GoToDateViewModelTests
{
    [Fact]
    public void Constructor_SeedsSelectedDateFromInitial()
    {
        var initial = new DateTime(2026, 3, 14, 10, 30, 0);
        var vm = new GoToDateViewModel(initial);

        Assert.Equal(initial.Date, vm.SelectedDate);   // time component dropped
    }

    [Fact]
    public void Confirm_WithDate_RaisesSavedWithDateOnly()
    {
        var vm = new GoToDateViewModel(DateTime.Today)
        {
            SelectedDate = new DateTime(2026, 7, 4, 13, 0, 0),
        };

        DateTime? saved = null;
        vm.Saved += d => saved = d;
        vm.ConfirmCommand.Execute(null);

        Assert.Equal(new DateTime(2026, 7, 4), saved);
    }

    [Fact]
    public void Confirm_WithNullDate_AnnouncesAndDoesNotRaiseSaved()
    {
        var vm = new GoToDateViewModel(DateTime.Today) { SelectedDate = null };

        bool saved = false;
        string? announced = null;
        AnnouncementCategory? cat = null;
        vm.Saved += _ => saved = true;
        vm.AnnouncementRequested += (t, c) => { announced = t; cat = c; };

        vm.ConfirmCommand.Execute(null);

        Assert.False(saved);
        Assert.Contains("Choose a date", announced);
        Assert.Equal(AnnouncementCategory.Result, cat);
    }

    [Fact]
    public void Cancel_RaisesCancelled()
    {
        var vm = new GoToDateViewModel(DateTime.Today);

        bool cancelled = false;
        vm.Cancelled += () => cancelled = true;
        vm.CancelCommand.Execute(null);

        Assert.True(cancelled);
    }
}
