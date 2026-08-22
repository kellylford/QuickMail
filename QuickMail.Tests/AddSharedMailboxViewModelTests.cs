using System;
using System.Collections.Generic;
using System.Linq;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class AddSharedMailboxViewModelTests
{
    // Work/school Microsoft on Graph (Microsoft OAuth). personal:true makes it a consumer account.
    private static AccountModel Graph(string name, bool personal = false) => new()
    {
        Id = Guid.NewGuid(), AccountName = name, Username = name.ToLowerInvariant() + "@work.com",
        BackendKind = BackendKind.MicrosoftGraph, AuthType = AuthType.OAuth2Microsoft,
        IsPersonalMicrosoftAccount = personal,
    };
    // Work/school Microsoft on Exchange IMAP (Microsoft OAuth) — the IMAP-parent shared case.
    private static AccountModel MsImap(string name) => new()
    {
        Id = Guid.NewGuid(), AccountName = name, Username = name.ToLowerInvariant() + "@work.com",
        BackendKind = BackendKind.ImapSmtp, AuthType = AuthType.OAuth2Microsoft,
    };
    // A non-Microsoft IMAP account (password auth) — Gmail app-password, Fastmail, "Other", etc.
    private static AccountModel Imap(string name) => new()
    {
        Id = Guid.NewGuid(), AccountName = name, Username = name.ToLowerInvariant() + "@host.com",
        BackendKind = BackendKind.ImapSmtp, AuthType = AuthType.Password,
    };
    private static AccountModel SharedOf(AccountModel parent, string addr) => new()
    {
        Id = Guid.NewGuid(), AccountName = addr, Username = addr, SharedAddress = addr,
        IsShared = true, ParentAccountId = parent.Id, BackendKind = parent.BackendKind,
    };

    [Fact]
    public void ParentOptions_OnlyWorkSchoolMicrosoft() // #31 — shared mailboxes exist only there
    {
        var workGraph    = Graph("WorkGraph");                       // work/school MS on Graph → IN
        var workImap     = MsImap("WorkImap");                       // work/school MS on Exchange IMAP → IN
        var personalGraph = Graph("PersonalG", personal: true);     // personal MS on Graph → OUT
        var personalImap = new AccountModel                          // personal MS on IMAP → OUT (the gap this closes)
        {
            Id = Guid.NewGuid(), AccountName = "PersonalI", Username = "me@outlook.com",
            BackendKind = BackendKind.ImapSmtp, AuthType = AuthType.OAuth2Microsoft,
            IsPersonalMicrosoftAccount = true,
        };
        var genericImap  = Imap("Fastmail");                        // non-Microsoft IMAP → OUT (RFC 2342, out of scope)
        var google       = new AccountModel                          // Gmail via Google OAuth → OUT
        {
            Id = Guid.NewGuid(), AccountName = "Gmail", Username = "me@gmail.com",
            BackendKind = BackendKind.ImapSmtp, AuthType = AuthType.OAuth2Google,
        };
        var pop3         = new AccountModel { Id = Guid.NewGuid(), AccountName = "Pop", Username = "me@pop.com", BackendKind = BackendKind.Pop3Smtp, AuthType = AuthType.Password };
        var shared       = SharedOf(workGraph, "support@work.com"); // a shared account itself → OUT

        var vm = new AddSharedMailboxViewModel(
            [workGraph, workImap, personalGraph, personalImap, genericImap, google, pop3, shared]);

        Assert.Contains(workGraph, vm.ParentOptions);
        Assert.Contains(workImap, vm.ParentOptions);
        Assert.DoesNotContain(personalGraph, vm.ParentOptions);   // personal MS has no shared mailboxes
        Assert.DoesNotContain(personalImap, vm.ParentOptions);    // ...on IMAP either — the gap this fix closes
        Assert.DoesNotContain(genericImap, vm.ParentOptions);     // non-Microsoft IMAP: RFC 2342 folders, not delegated mailboxes
        Assert.DoesNotContain(google, vm.ParentOptions);
        Assert.DoesNotContain(pop3, vm.ParentOptions);
        Assert.DoesNotContain(shared, vm.ParentOptions);
    }

    [Fact]
    public void ParentOptions_ExcludeUndetectedPersonalGraph_CaughtByDomainGuess() // #541
    {
        // Flag not detected yet (null), but a consumer-domain address → the domain-guess fallback
        // resolves it as personal, so it is excluded just like a confirmed personal account.
        var undetected = new AccountModel
        {
            Id = Guid.NewGuid(), AccountName = "Undetected", Username = "me@outlook.com",
            BackendKind = BackendKind.MicrosoftGraph, AuthType = AuthType.OAuth2Microsoft,
            // IsPersonalMicrosoftAccount left null — so only the consumer-domain guess can exclude it
        };
        var work = Graph("Work");

        var vm = new AddSharedMailboxViewModel([work, undetected]);

        Assert.Contains(work, vm.ParentOptions);
        Assert.DoesNotContain(undetected, vm.ParentOptions);
    }

    [Fact]
    public void ShowGraphPollNote_TrueForGraphParent_FalseForImap()
    {
        var vm = new AddSharedMailboxViewModel([Graph("Work"), MsImap("Home")]);

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
