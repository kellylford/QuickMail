using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The address book's People I've Sent To tab (#1039): the store's list and forget-one, the tab's view
/// model, and the real window — the row names a screen reader is given, Delete, and focus after a removal.
/// </summary>
// The window tests show an AddressBookWindow, so the class must not run beside the focus tests.
[Collection("WpfTests")]
public class PeopleSentToTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qm-sentto-" + Guid.NewGuid().ToString("N"));

    public PeopleSentToTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── The store ────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_IsMostRecentFirst_AndRemove_ForgetsOnePerson_UntilSentToAgain()
    {
        var ticks = 100L;
        using var store = new RecipientHistoryService(Path.Combine(_dir, "recipients.json")) { UtcTicks = () => ticks++ };
        await store.RecordAsync([("Ann", "ann@uni.edu")]);
        await store.RecordAsync([("Bob", "bob@uni.edu")]);
        await store.RecordAsync([("Cy", "cy@uni.edu")]);

        Assert.Equal(["cy@uni.edu", "bob@uni.edu", "ann@uni.edu"], (await store.ListAsync()).Select(p => p.EmailAddress));

        Assert.True(await store.RemoveAsync("BOB@uni.edu"));
        Assert.False(await store.RemoveAsync("bob@uni.edu"));   // already gone
        Assert.False(await store.RemoveAsync("  "));
        Assert.Equal(["cy@uni.edu", "ann@uni.edu"], (await store.ListAsync()).Select(p => p.EmailAddress));

        using (var reopened = new RecipientHistoryService(Path.Combine(_dir, "recipients.json")))
            Assert.Equal(2, await reopened.CountAsync());          // the forgetting was saved

        await store.RecordAsync([("Bob", "bob@uni.edu")]);       // sent to again: remembered again
        Assert.Equal("bob@uni.edu", (await store.ListAsync())[0].EmailAddress);
    }

    [Fact]
    public async Task Remove_WhenTheFileCannotBeWritten_ForgetsNoOne()
    {
        var path = Path.Combine(_dir, "recipients.json");
        using var store = new RecipientHistoryService(path);
        await store.RecordAsync([("Ann", "ann@uni.edu")]);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.ThrowsAnyAsync<IOException>(() => store.RemoveAsync("ann@uni.edu"));
        Assert.Single(await store.ListAsync());
    }

    // ── The tab's view model ─────────────────────────────────────────────────

    private sealed class History : IRecipientHistory
    {
        public List<ContactModel> People { get; } = [];
        public Task RecordAsync(IReadOnlyList<(string DisplayName, string EmailAddress)> recipients) => Task.CompletedTask;
        public Task<List<ContactModel>> SearchAsync(string text, int max, CancellationToken ct = default) => Task.FromResult(new List<ContactModel>());
        public Task<int> CountAsync() => Task.FromResult(People.Count);
        public Task ClearAsync() { People.Clear(); return Task.CompletedTask; }
        public Task<List<ContactModel>> ListAsync() => Task.FromResult(People.OrderByDescending(p => p.LastUsedTicks).ToList());
        public Task<bool> RemoveAsync(string emailAddress)
            => Task.FromResult(People.RemoveAll(p => p.EmailAddress.Equals(emailAddress, StringComparison.OrdinalIgnoreCase)) > 0);
    }

    private static readonly long Nov19 = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc).Ticks;

    private (PeopleSentToViewModel Vm, History History, ContactService Contacts) NewVm(bool remembering = true)
    {
        var history = new History();
        history.People.Add(new ContactModel { DisplayName = "Old Prof", EmailAddress = "old@uni.edu", LastUsedTicks = Nov19 - TimeSpan.TicksPerDay });
        history.People.Add(new ContactModel { DisplayName = "Jane Smith", EmailAddress = "jane@uni.edu", LastUsedTicks = Nov19 });
        history.People.Add(new ContactModel { DisplayName = "", EmailAddress = "registrar@uni.edu", LastUsedTicks = Nov19 - 2 * TimeSpan.TicksPerDay });
        history.People.Add(new ContactModel { DisplayName = "Al Contact", EmailAddress = "AL@uni.edu", LastUsedTicks = Nov19 + 1 });
        var contacts = new ContactService(new ProfileContext(Path.Combine(_dir, Guid.NewGuid().ToString("N"))));
        contacts.UpsertContactAsync(new ContactModel { DisplayName = "Al Contact", EmailAddress = "al@uni.edu" }).GetAwaiter().GetResult();
        return (new PeopleSentToViewModel(history, contacts, () => remembering), history, contacts);
    }

    [Fact]
    public async Task Load_ListsTheRemembered_NotInTheAddressBook_MostRecentFirst()
    {
        var (vm, _, _) = NewVm();
        await vm.LoadAsync();
        Assert.Equal(["jane@uni.edu", "old@uni.edu", "registrar@uni.edu"], vm.People.Select(p => p.EmailAddress));
        Assert.Same(vm.People[0], vm.SelectedPerson);
        Assert.Equal(string.Empty, vm.Notice);
        Assert.True(vm.HasPeople);
    }

    [Theory]
    [InlineData(true, 0, "No one yet.")]
    [InlineData(false, 0, "Remembering is off, so no one is here.")]
    [InlineData(false, 3, "Remembering is off, so no one new is added.")]
    public void TheNotice_SaysWhyTheListIsEmpty_OrThatRememberingIsOff(bool on, int count, string begins)
        => Assert.StartsWith(begins, PeopleSentToViewModel.NoticeFor(on, count), StringComparison.Ordinal);

    [Fact]
    public void TheNotice_IsEmpty_WhenRememberingAndThereArePeople()
        => Assert.Equal(string.Empty, PeopleSentToViewModel.NoticeFor(true, 2));

    [Fact]
    public async Task Forget_AsksFirst_ThenRemovesThePerson_AndLandsOnTheNext()
    {
        var (vm, history, _) = NewVm();
        await vm.LoadAsync();
        var said = new List<string>();
        vm.AnnouncementRequested += (t, _) => said.Add(t);
        string? asked = null;
        var answer = false;
        vm.ConfirmRequested += (_, q) => { asked = q; return Task.FromResult(answer); };
        RememberedPerson? landedOn = null;
        var stillThereWhenFocusMoved = false;
        vm.FocusNowRequested += () =>
        {
            landedOn = vm.SelectedPerson;
            stillThereWhenFocusMoved = vm.People.Any(p => p.EmailAddress == "jane@uni.edu");
            return true;
        };

        await vm.ForgetSelectedCommand.ExecuteAsync(null);
        Assert.Contains("Forget Jane Smith?", asked, StringComparison.Ordinal);
        Assert.Contains("remembered again if you send to them", asked, StringComparison.Ordinal);
        Assert.Equal(3, vm.People.Count);                      // declined: nothing changes
        Assert.Contains(history.People, p => p.EmailAddress == "jane@uni.edu");

        answer = true;
        await vm.ForgetSelectedCommand.ExecuteAsync(null);

        Assert.DoesNotContain(history.People, p => p.EmailAddress == "jane@uni.edu");
        Assert.Equal(["old@uni.edu", "registrar@uni.edu"], vm.People.Select(p => p.EmailAddress));
        Assert.Equal("old@uni.edu", landedOn?.EmailAddress);
        Assert.True(stillThereWhenFocusMoved);                 // focus moved before the row went (#265)
        Assert.Equal("Forgot Jane Smith.", vm.ResultText);
        Assert.Empty(said);                                    // focus landed, so the row is read instead
    }

    [Fact]
    public async Task ForgetTheLast_AnnouncesIt_AndSaysWhyTheListIsEmpty()
    {
        var history = new History();
        history.People.Add(new ContactModel { DisplayName = "", EmailAddress = "only@uni.edu", LastUsedTicks = Nov19 });
        var vm = new PeopleSentToViewModel(history, new StubContactService(), () => true);
        await vm.LoadAsync();
        var said = new List<string>();
        var queued = 0;
        vm.AnnouncementRequested += (t, _) => said.Add(t);
        vm.FocusRequested += () => queued++;
        vm.ConfirmRequested += (_, _) => Task.FromResult(true);

        await vm.ForgetSelectedCommand.ExecuteAsync(null);

        Assert.Empty(vm.People);
        Assert.False(vm.HasPeople);
        Assert.Null(vm.SelectedPerson);
        Assert.Equal(1, queued);                              // nowhere to land: the list takes focus
        Assert.Equal(["Forgot only@uni.edu."], said);
        Assert.StartsWith("No one yet.", vm.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveToContacts_AddsTheContact_TakesThemOffThisList_AndTellsTheContactsTab()
    {
        var (vm, history, contacts) = NewVm();
        await vm.LoadAsync();
        var refreshed = 0;
        vm.ContactsChanged += () => { refreshed++; return Task.CompletedTask; };

        await vm.SaveToContactsCommand.ExecuteAsync(null);

        var saved = Assert.Single(await contacts.SearchContactsAsync("jane", Ct));
        Assert.Equal("Jane Smith", saved.DisplayName);
        Assert.Equal(Nov19, saved.LastUsedTicks);
        Assert.DoesNotContain(vm.People, p => p.EmailAddress == "jane@uni.edu");
        Assert.Contains(history.People, p => p.EmailAddress == "jane@uni.edu"); // still remembered, harmlessly
        Assert.Equal(1, refreshed);
        Assert.Equal("Added Jane Smith to your contacts.", vm.ResultText);

        await vm.LoadAsync();                                   // and a reload keeps them off
        Assert.DoesNotContain(vm.People, p => p.EmailAddress == "jane@uni.edu");
    }

    [Fact]
    public async Task ForgetAll_AsksFirst_ThenClearsEveryone()
    {
        var (vm, history, _) = NewVm(remembering: false);
        await vm.LoadAsync();
        Assert.StartsWith("Remembering is off, so no one new is added.", vm.Notice, StringComparison.Ordinal);
        var said = new List<string>();
        vm.AnnouncementRequested += (t, _) => said.Add(t);
        string? asked = null;
        vm.ConfirmRequested += (_, q) => { asked = q; return Task.FromResult(true); };

        await vm.ForgetAllCommand.ExecuteAsync(null);

        Assert.Contains("all 4 people", asked, StringComparison.Ordinal); // the store's count, contacts included
        Assert.Empty(history.People);
        Assert.Empty(vm.People);
        Assert.Equal(["Forgot 4 people."], said);
        Assert.StartsWith("Remembering is off, so no one is here.", vm.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Insert_AddsTheSelectedPerson_ToTheChosenField()
    {
        var (vm, _, _) = NewVm();
        await vm.LoadAsync();
        Assert.False(vm.HasInsertActions);
        var to = new List<string>();
        var cc = new List<string>();
        vm.SetInsertActions(c => to.Add(c.EmailAddress), c => cc.Add(c.EmailAddress), _ => { });
        Assert.True(vm.HasInsertActions);
        vm.AddToToCommand.Execute(null);
        vm.AddToCcCommand.Execute(null);
        Assert.Equal(["jane@uni.edu"], to);
        Assert.Equal(["jane@uni.edu"], cc);
    }

    [Fact]
    public void ARow_ReadsNameAddressAndWhen_WithOrWithoutFieldLabels()
    {
        var when = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
        var local = when.ToLocalTime().ToString("d MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture);
        var jane = new RememberedPerson("Jane Smith", "jane@uni.edu", when.Ticks);
        Assert.Equal($"Jane Smith, jane@uni.edu, last sent {local}", jane.AccessibleName);
        Assert.Equal(jane.AccessibleName, jane.ToString());
        Assert.Equal("Jane Smith", jane.TypeAheadText);

        var bare = new RememberedPerson("", "registrar@uni.edu", when.Ticks, showFieldLabels: true);
        Assert.Equal($"Email registrar@uni.edu, last sent {local}", bare.AccessibleName);
        Assert.Equal("registrar@uni.edu", bare.TypeAheadText);
        Assert.Equal("Name Jane Smith, email jane@uni.edu, last sent " + local,
            new RememberedPerson("Jane Smith", "jane@uni.edu", when.Ticks, showFieldLabels: true).AccessibleName);
    }

    [Fact]
    public void TheAddressBook_HasTheTab_OnlyWithAStore()
    {
        Assert.False(new AddressBookViewModel(new StubContactService()).HasSentTo);
        var vm = new AddressBookViewModel(new StubContactService(), recipientHistory: new History());
        Assert.True(vm.HasSentTo);
        Assert.NotNull(vm.SentTo);
    }

    // ── The real window ──────────────────────────────────────────────────────

    private (AddressBookWindow Window, AddressBookViewModel Vm, History History) BuildWindow()
    {
        WpfTestHost.EnsureApplication();
        WpfTestHost.EnsureStyles("AccessibleStyles", "ThemedControls");
        var history = new History();
        history.People.Add(new ContactModel { DisplayName = "Jane Smith", EmailAddress = "jane@uni.edu", LastUsedTicks = Nov19 });
        history.People.Add(new ContactModel { DisplayName = "", EmailAddress = "registrar@uni.edu", LastUsedTicks = Nov19 - TimeSpan.TicksPerDay });
        var svc = new ContactService(new ProfileContext(_dir));
        var vm = new AddressBookViewModel(svc, recipientHistory: history);
        var window = new AddressBookWindow(vm)
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ConfirmForTests = _ => true,
        };
        vm.LoadAsync().GetAwaiter().GetResult();
        window.Show();
        var tabs = window.FindName("MainTabs") as TabControl;
        Assert.NotNull(tabs);
        tabs!.SelectedItem = window.FindName("SentToTab");
        window.UpdateLayout();
        PumpUntil(() => (window.FindName("SentToList") as ListView)?.Items.Count == 2);
        return (window, vm, history);
    }

    [StaFact]
    public void Window_RowsSpeakThePerson_FromTheContainer()
    {
        var (window, _, _) = BuildWindow();
        try
        {
            var list = window.FindName("SentToList") as ListView;
            Assert.NotNull(list);
            list!.UpdateLayout();
            var names = (UIElementAutomationPeer.CreatePeerForElement(list).GetChildren() ?? [])
                .Select(p => p.GetName()).ToList();
            Assert.Equal(2, names.Count);
            Assert.StartsWith("Jane Smith, jane@uni.edu, last sent ", names[0], StringComparison.Ordinal);
            Assert.StartsWith("registrar@uni.edu, last sent ", names[1], StringComparison.Ordinal);
            Assert.DoesNotContain(names, n => n.Contains("QuickMail.", StringComparison.Ordinal));

            // On the container, not inside the template (#644).
            var row = list.ItemContainerGenerator.ContainerFromIndex(0) as ListViewItem;
            Assert.NotNull(row);
            Assert.StartsWith("Jane Smith", System.Windows.Automation.AutomationProperties.GetName(row!), StringComparison.Ordinal);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void Window_DeleteInTheList_ForgetsThePerson_AndFocusMovesToTheNext()
    {
        var (window, vm, history) = BuildWindow();
        try
        {
            var list = (ListView)window.FindName("SentToList");
            var first = (ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            first.Focus();
            PumpUntil(() => first.IsKeyboardFocusWithin);

            var delete = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(list)!, 0, Key.Delete)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            first.RaiseEvent(delete);
            Assert.True(delete.Handled);
            PumpUntil(() => list.Items.Count == 1);

            Assert.DoesNotContain(history.People, p => p.EmailAddress == "jane@uni.edu");
            var survivor = (ListViewItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            PumpUntil(() => survivor.IsKeyboardFocusWithin);
            Assert.Equal("registrar@uni.edu", vm.SentTo!.SelectedPerson!.EmailAddress);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void Window_WithNoStore_HidesTheTab()
    {
        WpfTestHost.EnsureApplication();
        var vm = new AddressBookViewModel(new StubContactService());
        var window = new AddressBookWindow(vm) { WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            var tab = window.FindName("SentToTab") as TabItem;
            Assert.NotNull(tab);
            Assert.Equal(Visibility.Collapsed, tab!.Visibility);
        }
        finally { window.Close(); }
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        DoEvents();
        while (!condition())
        {
            if (clock.Elapsed >= TimeSpan.FromSeconds(5)) Assert.Fail("Condition was not met within five seconds.");
            Thread.Sleep(10);
            DoEvents();
        }
    }

    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
