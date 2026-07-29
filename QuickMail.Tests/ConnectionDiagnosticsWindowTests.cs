using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The Connection Diagnostics window is the surface the user reaches for at the moment the symptom
/// appears, so its XAML must load and its named elements must exist. Element lookups use
/// <c>as</c> + <c>Assert.NotNull</c> per CLAUDE.md, so a renamed element fails cleanly instead of
/// throwing a bare NullReferenceException.
/// </summary>
// WpfTests because it shows real Windows on an STA thread. That does mean it can run alongside
// the ConnectionDiagnostics collection, which mutates the same static journal — so these tests
// deliberately assert nothing about journal CONTENT, only about the window and its bound VM.
[Collection("WpfTests")]
public class ConnectionDiagnosticsWindowTests
{
    private static IReadOnlyList<AccountModel> TwoAccounts() =>
    [
        new() { Id = Guid.NewGuid(), AccountName = "Kelly", ImapHost = "mail.example.com", IsConnected = false },
        new() { Id = Guid.NewGuid(), AccountName = "Idea Place", ImapHost = "mail.example.net", IsConnected = true },
    ];

    [StaFact]
    public void Window_LoadsAndExposesItsNamedElements()
    {
        var window = new ConnectionDiagnosticsWindow(probe: null, TwoAccounts)
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
        };

        try
        {
            var accountList = window.FindName("AccountList") as ListBox;
            var eventList   = window.FindName("EventList") as ListBox;
            var filter      = window.FindName("FilterCombo") as ComboBox;
            var buttonRow   = window.FindName("ButtonRow") as FrameworkElement;

            Assert.NotNull(accountList);
            Assert.NotNull(eventList);
            Assert.NotNull(filter);
            Assert.NotNull(buttonRow);   // the third F6 stop

            foreach (var name in new[] { "TestButton", "CopyButton", "SaveButton", "RefreshButton", "CloseButton" })
                Assert.NotNull(window.FindName(name) as Button);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void Window_ListsEveryAccountWithItsDisplayedStatus()
    {
        var accounts = TwoAccounts();
        var window = new ConnectionDiagnosticsWindow(probe: null, () => accounts)
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
        };

        try
        {
            var vm = Assert.IsType<ConnectionDiagnosticsViewModel>(window.DataContext);

            Assert.Equal(2, vm.Accounts.Count);
            Assert.Equal("disconnected", vm.Accounts[0].StatusText);
            Assert.Equal("connected", vm.Accounts[1].StatusText);

            // "All accounts" plus one entry per account.
            Assert.Equal(3, vm.FilterOptions.Count);
            Assert.Equal("All accounts", vm.FilterOptions[0]);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void Window_WithoutAProbe_CannotRunATest()
    {
        // The button must be disabled rather than throwing when no probe was supplied (the stub
        // paths and any future backend that cannot probe).
        var window = new ConnectionDiagnosticsWindow(probe: null, TwoAccounts)
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
        };

        try
        {
            var vm = Assert.IsType<ConnectionDiagnosticsViewModel>(window.DataContext);
            Assert.NotNull(vm.SelectedAccount);
            Assert.False(vm.TestAccountCommand.CanExecute(null));
        }
        finally { window.Close(); }
    }
}

/// <summary>
/// ViewModel-level behaviour that needs no window and so needs no STA thread.
/// </summary>
[Collection("ConnectionDiagnostics")]
public class ConnectionDiagnosticsViewModelTests : IDisposable
{
    public ConnectionDiagnosticsViewModelTests() => ConnectionJournal.ResetForTests();
    public void Dispose() => ConnectionJournal.ResetForTests();

    private static AccountModel Account(string name, bool connected) =>
        new() { Id = Guid.NewGuid(), AccountName = name, ImapHost = "h", IsConnected = connected };

    [Fact]
    public void Events_AreNewestFirst()
    {
        ConnectionJournal.Record(ConnectionEventKind.Pool, "Kelly", "h", "older");
        ConnectionJournal.Record(ConnectionEventKind.Pool, "Kelly", "h", "newer");

        var vm = new ConnectionDiagnosticsViewModel(null, () => new[] { Account("Kelly", true) });

        Assert.Equal("newer", vm.Events[0].Phase);
        Assert.Equal("older", vm.Events[1].Phase);
    }

    [Fact]
    public void EventFilter_LimitsTheListToOneAccount()
    {
        ConnectionJournal.Record(ConnectionEventKind.Pool, "Kelly", "h", "kelly-event");
        ConnectionJournal.Record(ConnectionEventKind.Pool, "Idea Place", "h", "idea-event");

        var vm = new ConnectionDiagnosticsViewModel(
            null, () => new[] { Account("Kelly", true), Account("Idea Place", true) });

        Assert.Equal(2, vm.Events.Count);

        vm.EventFilter = "Kelly";
        Assert.Equal("kelly-event", Assert.Single(vm.Events).Phase);
    }

    [Fact]
    public void AppendEvent_RespectsTheActiveFilter()
    {
        var vm = new ConnectionDiagnosticsViewModel(
            null, () => new[] { Account("Kelly", true), Account("Idea Place", true) })
        {
            EventFilter = "Kelly",
        };

        vm.AppendEvent(new ConnectionEvent(DateTime.Now, ConnectionEventKind.Pool, "Idea Place", "h", "other", ""));
        Assert.Empty(vm.Events);

        vm.AppendEvent(new ConnectionEvent(DateTime.Now, ConnectionEventKind.Pool, "Kelly", "h", "mine", ""));
        Assert.Equal("mine", Assert.Single(vm.Events).Phase);
    }

    [Fact]
    public void UnprobedAccount_SaysSoRatherThanImplyingAResult()
    {
        var vm = new ConnectionDiagnosticsViewModel(null, () => new[] { Account("Kelly", false) });

        Assert.Equal("Not yet tested.", vm.Accounts[0].VerdictText);
    }

    [Fact]
    public void CopyReport_HandsTheViewACompleteReport()
    {
        ConnectionJournal.Record(ConnectionEventKind.Connect, "Kelly", "h", "connect-failed", "boom");

        var vm = new ConnectionDiagnosticsViewModel(null, () => new[] { Account("Kelly", false) });

        string? copied = null;
        vm.CopyRequested += text => copied = text;
        vm.CopyReportCommand.Execute(null);

        Assert.NotNull(copied);
        Assert.Contains("QuickMail connection diagnostics report", copied);
        Assert.Contains("Kelly", copied);
        Assert.Contains("DISCONNECTED", copied);       // the account summary states what is shown
        Assert.Contains("connect-failed", copied);     // and the journal is included
        Assert.Contains("Hosts and live socket counts", copied);
    }

    [Fact]
    public void CopyReport_AnnouncesTheResult()
    {
        var vm = new ConnectionDiagnosticsViewModel(null, () => new[] { Account("Kelly", true) });

        (string Text, AnnouncementCategory Category)? announced = null;
        vm.AnnounceRequested += (t, c) => announced = (t, c);
        vm.CopyReportCommand.Execute(null);

        Assert.NotNull(announced);
        Assert.Contains("Report copied", announced!.Value.Text);
        Assert.Equal(AnnouncementCategory.Result, announced.Value.Category);
    }
}
