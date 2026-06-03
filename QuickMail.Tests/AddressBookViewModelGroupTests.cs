using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class AddressBookViewModelGroupTests
{
    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), $"QM-AddrBookGroupTests-{Guid.NewGuid():N}");

    private static (AddressBookViewModel vm, ContactService service, string dir) MakeViewModel()
    {
        var dir = TempDir();
        var profile = new ProfileContext(dir);
        var service = new ContactService(profile);
        var vm = new AddressBookViewModel(service);
        return (vm, service, dir);
    }

    private static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    // Per-test counter for unique emails. Built by string concatenation so no
    // literal email in the source can be obfuscated into the same value.
    private static int _nextId;
    private static int NextId() => Interlocked.Increment(ref _nextId);

    private static ContactModel MakeContact(string name)
    {
        var id = NextId();
        return new ContactModel
        {
            DisplayName   = name,
            EmailAddress  = $"{char.ToLowerInvariant(name[0])}{id}@local.test",
            LastUsedTicks = DateTimeOffset.UtcNow.UtcTicks,
        };
    }

    [Fact]
    public async Task CreateGroupCommand_AddsGroupToList()
    {
        var (vm, _, dir) = MakeViewModel();
        try
        {
            vm.NewGroupName = "Friends";
            vm.CreateGroupCommand.Execute(null);
            await Task.Delay(50);
            Assert.NotEmpty(vm.Groups);
            Assert.Equal("Friends", vm.Groups[0].Name);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task CreateGroupCommand_IgnoresEmptyName()
    {
        var (vm, _, dir) = MakeViewModel();
        try
        {
            vm.NewGroupName = "   ";
            vm.CreateGroupCommand.Execute(null);
            await Task.Delay(50);
            Assert.Empty(vm.Groups);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task DeleteGroupCommand_RemovesGroup()
    {
        var (vm, _, dir) = MakeViewModel();
        try
        {
            vm.ConfirmRequested += (_, _) => Task.FromResult(true);
            vm.NewGroupName = "ToDelete";
            vm.CreateGroupCommand.Execute(null);
            await Task.Delay(50);
            Assert.Single(vm.Groups);
            vm.SelectedGroup = vm.Groups[0];
            vm.DeleteGroupCommand.Execute(null);
            await Task.Delay(50);
            Assert.Empty(vm.Groups);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task SelectedGroup_ResolvesMembers()
    {
        var (vm, service, dir) = MakeViewModel();
        try
        {
            await service.UpsertContactAsync(MakeContact("Alice"));
            await service.UpsertContactAsync(MakeContact("Bob"));
            var groupId = await service.CreateGroupAsync("Pair");
            await service.AddMemberAsync(groupId, 1);
            await service.AddMemberAsync(groupId, 2);
            await vm.LoadAsync();
            vm.SelectedGroup = vm.Groups.First(g => g.Id == groupId);
            await Task.Delay(50);
            Assert.Equal(2, vm.SelectedGroupMembers.Count);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task AddContactToGroupAsync_AppendsMember()
    {
        var (vm, service, dir) = MakeViewModel();
        try
        {
            await service.UpsertContactAsync(MakeContact("Alice"));
            await service.UpsertContactAsync(MakeContact("Bob"));
            var groupId = await service.CreateGroupAsync("Pair");
            await service.AddMemberAsync(groupId, 1);
            await vm.LoadAsync();
            vm.SelectedGroup = vm.Groups.First(g => g.Id == groupId);
            await Task.Delay(50);
            var bob = vm.FilteredContacts.First(c => c.DisplayName == "Bob");
            vm.SelectedContact = bob;
            var targetGroup = vm.Groups.First(g => g.Id == groupId);
            await vm.AddContactToGroupAsync(targetGroup);
            await Task.Delay(50);
            var group = await service.LoadAllGroupsAsync();
            Assert.Equal(2, group.First(g => g.Id == groupId).MemberContactIds.Count);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task RemoveContactFromGroupAsync_DropsMember()
    {
        var (vm, service, dir) = MakeViewModel();
        try
        {
            await service.UpsertContactAsync(MakeContact("Alice"));
            await service.UpsertContactAsync(MakeContact("Bob"));
            var groupId = await service.CreateGroupAsync("Pair");
            await service.AddMemberAsync(groupId, 1);
            await service.AddMemberAsync(groupId, 2);
            await vm.LoadAsync();
            vm.SelectedGroup = vm.Groups.First(g => g.Id == groupId);
            await Task.Delay(50);
            var alice = vm.SelectedGroupMembers.First(c => c.DisplayName == "Alice");
            await vm.RemoveContactFromGroupAsync(alice);
            await Task.Delay(50);
            var group = await service.LoadAllGroupsAsync();
            Assert.Single(group.First(g => g.Id == groupId).MemberContactIds);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task DeleteConfirmedAsync_RefreshesGroupsList()
    {
        var (vm, service, dir) = MakeViewModel();
        try
        {
            await service.UpsertContactAsync(MakeContact("Alice"));
            await service.CreateGroupAsync("G");
            await service.AddMemberAsync(1, 1);
            await vm.LoadAsync();
            Assert.Single(vm.Groups);
            Assert.Single(vm.FilteredContacts);
            vm.SelectedContact = vm.FilteredContacts[0];
            await vm.DeleteConfirmedAsync();
            // After deleting Alice, the group should still exist on disk,
            // but its display should now report zero resolved members.
            var groups = await service.LoadAllGroupsAsync();
            Assert.Single(groups);
            Assert.Equal(0, groups[0].ResolvedMemberCount);
            Assert.Equal(1, groups[0].MissingContactCount);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task LoadAsync_PopulatesGroupsFromService()
    {
        var (vm, service, dir) = MakeViewModel();
        try
        {
            await service.CreateGroupAsync("G1");
            await service.CreateGroupAsync("G2");
            await vm.LoadAsync();
            Assert.Equal(2, vm.Groups.Count);
        }
        finally { Cleanup(dir); }
    }
}
