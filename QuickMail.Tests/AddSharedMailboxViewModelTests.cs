using System;
using System.Collections.Generic;
using System.Linq;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class AddSharedMailboxViewModelTests
{
    private static AccountModel Graph(string name, bool personal = false) => new()
    {
        Id = Guid.NewGuid(), AccountName = name, Username = name.ToLowerInvariant() + "@work.com",
        BackendKind = BackendKind.MicrosoftGraph, IsPersonalMicrosoftAccount = personal,
    };
    private static AccountModel Imap(string name) => new()
    {
        Id = Guid.NewGuid(), AccountName = name, Username = name.ToLowerInvariant() + "@host.com",
        BackendKind = BackendKind.ImapSmtp,
    };
    private static AccountModel SharedOf(AccountModel parent, string addr) => new()
    {
        Id = Guid.NewGuid(), AccountName = addr, Username = addr, SharedAddress = addr,
        IsShared = true, ParentAccountId = parent.Id, BackendKind = parent.BackendKind,
    };

    [Fact]
    public void ParentOptions_ExcludeSharedAndPersonalMicrosoft()
    {
        var work = Graph("Work");
        var personal = Graph("Personal", personal: true);
        var imap = Imap("Home");
        var shared = SharedOf(work, "support@work.com");

        var vm = new AddSharedMailboxViewModel([work, personal, imap, shared]);

        Assert.Contains(work, vm.ParentOptions);
        Assert.Contains(imap, vm.ParentOptions);
        Assert.DoesNotContain(personal, vm.ParentOptions);   // personal MS has no shared mailboxes
        Assert.DoesNotContain(shared, vm.ParentOptions);     // a shared account can't be a parent
    }

    [Fact]
    public void ShowGraphPollNote_TrueForGraphParent_FalseForImap()
    {
        var vm = new AddSharedMailboxViewModel([Graph("Work"), Imap("Home")]);

        vm.SelectedParent = vm.ParentOptions.First(a => a.BackendKind == BackendKind.MicrosoftGraph);
        Assert.True(vm.ShowGraphPollNote);

        vm.SelectedParent = vm.ParentOptions.First(a => a.BackendKind == BackendKind.ImapSmtp);
        Assert.False(vm.ShowGraphPollNote);
    }

    [Fact]
    public void Add_ValidAddress_RaisesEvent_WithSharedAccountLinkedToParent()
    {
        var work = Graph("Work");
        var vm = new AddSharedMailboxViewModel([work], preferredParentId: work.Id)
        {
            Address = "support@work.com",
        };
        AccountModel? created = null;
        vm.SharedMailboxAdded += a => created = a;

        vm.AddCommand.Execute(null);

        Assert.NotNull(created);
        Assert.True(created!.IsShared);
        Assert.Equal(work.Id, created.ParentAccountId);
        Assert.Equal("support@work.com", created.SharedAddress);
        Assert.Equal(BackendKind.MicrosoftGraph, created.BackendKind);   // follows the parent
        Assert.Empty(vm.ErrorText);
    }

    [Fact]
    public void Add_InvalidAddress_SetsError_NoEvent()
    {
        var vm = new AddSharedMailboxViewModel([Graph("Work")]) { Address = "not-an-email" };
        vm.SelectedParent = vm.ParentOptions.First();
        var raised = false;
        vm.SharedMailboxAdded += _ => raised = true;

        vm.AddCommand.Execute(null);

        Assert.False(raised);
        Assert.Contains("valid email", vm.ErrorText);
    }

    [Fact]
    public void Add_DuplicateAddress_SetsError_NoEvent()
    {
        var work = Graph("Work");                       // work@work.com already exists
        var vm = new AddSharedMailboxViewModel([work]) { Address = "work@work.com" };
        vm.SelectedParent = vm.ParentOptions.First();
        var raised = false;
        vm.SharedMailboxAdded += _ => raised = true;

        vm.AddCommand.Execute(null);

        Assert.False(raised);
        Assert.Contains("already exists", vm.ErrorText);
    }

    [Fact]
    public void AddCommand_Disabled_UntilAddressAndParentPresent()
    {
        var vm = new AddSharedMailboxViewModel([Graph("Work")]);
        vm.SelectedParent = null;
        Assert.False(vm.AddCommand.CanExecute(null));

        vm.SelectedParent = vm.ParentOptions.First();
        Assert.False(vm.AddCommand.CanExecute(null));   // still no address

        vm.Address = "support@work.com";
        Assert.True(vm.AddCommand.CanExecute(null));
    }
}
