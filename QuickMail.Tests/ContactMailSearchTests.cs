// Tests for "find mail from / to this contact" — issue #370.
//
// The address book hands the main window a contact; the main window opens a virtual
// folder whose sentinel carries the address and the direction, and the fetch filters
// every cached message across all accounts and folders on the From or To header.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class ContactMailSearchTests
{
    private static MailMessageSummary Msg(string id, string from, string to, int daysAgo = 0) => new()
    {
        MessageId  = id,
        AccountId  = Guid.NewGuid(),
        FolderName = "INBOX",
        From       = from,
        To         = to,
        Subject    = $"Subject {id}",
        Date       = DateTimeOffset.Now.AddDays(-daysAgo),
    };

    private static readonly MailMessageSummary[] Corpus =
    [
        Msg("1", "Bob Baker <bob@example.com>", "kelly@example.com",                       daysAgo: 2),
        Msg("2", "Kelly <kelly@example.com>",   "Bob Baker <bob@example.com>, ann@x.com",  daysAgo: 1),
        Msg("3", "Ann <ann@x.com>",             "kelly@example.com",                       daysAgo: 3),
        Msg("4", "BOB@EXAMPLE.COM",             "kelly@example.com",                       daysAgo: 0),
    ];

    private static MainViewModel MakeVm(IEnumerable<MailMessageSummary>? messages = null)
    {
        ILocalStoreService store = messages != null
            ? new FilterableStoreForFlags(messages)
            : new StubLocalStoreService();
        return new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            store, new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(), new StubSmtpService());
    }

    [Fact]
    public async Task ShowContactMail_From_ReturnsOnlyMessagesTheContactSent()
    {
        var vm = MakeVm(Corpus);
        await vm.InitialLoadAsync();

        await vm.ShowContactMailAsync("bob@example.com", MainViewModel.ContactMailDirection.From, "Bob Baker");

        Assert.Equal(new[] { "4", "1" }, vm.Messages.Select(m => m.MessageId).ToArray());
    }

    [Fact]
    public async Task ShowContactMail_To_ReturnsOnlyMessagesAddressedToTheContact()
    {
        var vm = MakeVm(Corpus);
        await vm.InitialLoadAsync();

        await vm.ShowContactMailAsync("bob@example.com", MainViewModel.ContactMailDirection.To, "Bob Baker");

        Assert.Single(vm.Messages);
        Assert.Equal("2", vm.Messages[0].MessageId);
    }

    [Fact]
    public async Task ShowContactMail_UsesContactNameInTheFolderTitle()
    {
        var vm = MakeVm(Corpus);
        await vm.InitialLoadAsync();

        await vm.ShowContactMailAsync("bob@example.com", MainViewModel.ContactMailDirection.From, "Bob Baker");

        Assert.Equal("Mail from Bob Baker", vm.SelectedFolder?.DisplayName);
        Assert.Contains("2 messages from bob@example.com", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShowContactMail_NoName_TitlesWithTheAddress()
    {
        var vm = MakeVm(Corpus);
        await vm.InitialLoadAsync();

        await vm.ShowContactMailAsync("bob@example.com", MainViewModel.ContactMailDirection.To);

        Assert.Equal("Mail to bob@example.com", vm.SelectedFolder?.DisplayName);
    }

    [Fact]
    public async Task ShowContactMail_NoMatches_ClearsListAndSaysSo()
    {
        var vm = MakeVm(Corpus);
        await vm.InitialLoadAsync();

        await vm.ShowContactMailAsync("nobody@example.com", MainViewModel.ContactMailDirection.From);

        Assert.Empty(vm.Messages);
        Assert.Equal("No messages from nobody@example.com.", vm.StatusText);
    }

    [Fact]
    public async Task ShowContactMail_BlankAddress_DoesNothing()
    {
        var vm = MakeVm(Corpus);
        await vm.InitialLoadAsync();
        var before = vm.SelectedFolder;

        await vm.ShowContactMailAsync("   ", MainViewModel.ContactMailDirection.From);

        Assert.Same(before, vm.SelectedFolder);
    }

    [Fact]
    public async Task ShowContactMail_ClearsAnyActiveFilterAndSearch()
    {
        // The results view is a fresh start, exactly like navigating to any other folder:
        // a leftover unread filter or search text must not silently hide matches.
        var vm = MakeVm(Corpus);
        await vm.InitialLoadAsync();
        await vm.SetFilterCommand.ExecuteAsync("unread");
        vm.IsSearchActive = true;
        vm.SearchText     = "zzz";

        await vm.ShowContactMailAsync("bob@example.com", MainViewModel.ContactMailDirection.From);

        Assert.Equal(MessageFilter.All, vm.ActiveFilter);
        Assert.Equal(string.Empty, vm.SearchText);
        Assert.False(vm.IsSearchActive);
        Assert.Equal(2, vm.Messages.Count);
    }

    [Fact]
    public async Task SelectingTheSentinelFolder_RunsTheSameSearch()
    {
        // Refresh, sync-days changes, and any other re-fetch go back through the sentinel,
        // so the folder name has to round-trip the address (including characters that
        // percent-escape) and the direction.
        var messages = new[]
        {
            Msg("1", "Bob <bob+news@example.com>", "kelly@example.com"),
            Msg("2", "Ann <ann@x.com>",            "kelly@example.com"),
        };
        var vm = MakeVm(messages);
        await vm.InitialLoadAsync();

        var folder = MainViewModel.CreateContactMailVirtualFolder(
            "bob+news@example.com", MainViewModel.ContactMailDirection.From, "Bob");
        Assert.DoesNotContain("+", folder.FullName, StringComparison.Ordinal);

        await vm.SelectFolderCommand.ExecuteAsync(folder);

        Assert.Single(vm.Messages);
        Assert.Equal("1", vm.Messages[0].MessageId);
    }

    // ── Address book side ────────────────────────────────────────────────────

    [Fact]
    public void AddressBook_WithoutSearchActions_HidesTheFindMailActions()
    {
        var vm = new AddressBookViewModel(new StubContactService())
        {
            SelectedContact = new ContactModel { DisplayName = "Bob", EmailAddress = "bob@example.com" },
        };

        Assert.False(vm.HasSearchActions);
        Assert.False(vm.CanFindMailForContact);
        // Invoking anyway must not throw — the command is registered in the palette.
        vm.FindMailFromContactCommand.Execute(null);
    }

    [Fact]
    public void AddressBook_FindMailCommands_ReportTheSelectedContact()
    {
        ContactModel? fromArg = null;
        ContactModel? toArg   = null;
        var vm = new AddressBookViewModel(new StubContactService());
        vm.SetSearchActions(c => fromArg = c, c => toArg = c);

        var bob = new ContactModel { DisplayName = "Bob", EmailAddress = "bob@example.com" };
        vm.SelectedContact = bob;

        Assert.True(vm.CanFindMailForContact);
        vm.FindMailFromContactCommand.Execute(null);
        vm.FindMailToContactCommand.Execute(null);

        Assert.Same(bob, fromArg);
        Assert.Same(bob, toArg);
    }

    [Fact]
    public void AddressBook_ContactWithNoAddress_CannotBeSearchedFor()
    {
        var invoked = false;
        var vm = new AddressBookViewModel(new StubContactService());
        vm.SetSearchActions(_ => invoked = true, _ => invoked = true);

        vm.SelectedContact = new ContactModel { DisplayName = "No Address", EmailAddress = string.Empty };

        Assert.False(vm.CanFindMailForContact);
        vm.FindMailFromContactCommand.Execute(null);
        vm.FindMailToContactCommand.Execute(null);
        Assert.False(invoked);
    }

    [Fact]
    public void AddressBook_CanFindMail_TracksSelection()
    {
        var vm = new AddressBookViewModel(new StubContactService());
        vm.SetSearchActions(_ => { }, _ => { });
        var raised = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AddressBookViewModel.CanFindMailForContact)) raised++;
        };

        vm.SelectedContact = new ContactModel { DisplayName = "Bob", EmailAddress = "bob@example.com" };
        Assert.True(vm.CanFindMailForContact);

        vm.SelectedContact = null;
        Assert.False(vm.CanFindMailForContact);
        Assert.Equal(2, raised);
    }
}
