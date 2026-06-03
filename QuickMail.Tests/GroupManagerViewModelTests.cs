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

/// <summary>
/// Tests for the standalone GroupManagerViewModel: rename, add-member, remove-member.
/// </summary>
public class GroupManagerViewModelTestsV2
{
    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), $"QM-GroupManagerTests-{Guid.NewGuid():N}");

    private static (ContactService service, string dir) MakeService()
    {
        var dir = TempDir();
        var profile = new ProfileContext(dir);
        var service = new ContactService(profile);
        return (service, dir);
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
    public async Task LoadAsync_PopulatesGroupsAndCandidates()
    {
        var (service, dir) = MakeService();
        try
        {
            await service.CreateGroupAsync("G1");
            await service.UpsertContactAsync(MakeContact("Alice"));
            var vm = new GroupManagerViewModel(service);
            await vm.LoadAsync();
            Assert.Single(vm.Groups);
            Assert.Single(vm.ContactCandidates);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task RenameAsync_UpdatesName()
    {
        var (service, dir) = MakeService();
        try
        {
            var gid = await service.CreateGroupAsync("Old");
            var vm = new GroupManagerViewModel(service);
            await vm.LoadAsync();
            vm.SelectedGroup = vm.Groups[0];
            vm.NewName = "New";
            await vm.RenameSelectedCommand.ExecuteAsync(null);
            var groups = await service.LoadAllGroupsAsync();
            Assert.Equal("New", groups.First(g => g.Id == gid).Name);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task DeleteAsync_RemovesGroup()
    {
        var (service, dir) = MakeService();
        try
        {
            var gid = await service.CreateGroupAsync("G");
            var vm = new GroupManagerViewModel(service);
            vm.ConfirmRequested += (_, _) => Task.FromResult(true);
            await vm.LoadAsync();
            vm.SelectedGroup = vm.Groups[0];
            await vm.DeleteSelectedCommand.ExecuteAsync(null);
            var groups = await service.LoadAllGroupsAsync();
            Assert.Empty(groups);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task AddMemberAsync_Adds()
    {
        var (service, dir) = MakeService();
        try
        {
            await service.UpsertContactAsync(MakeContact("Alice"));
            var gid = await service.CreateGroupAsync("G");
            var vm = new GroupManagerViewModel(service);
            await vm.LoadAsync();
            vm.SelectedGroup = vm.Groups[0];
            var alice = vm.ContactCandidates[0];
            await vm.AddMemberCommand.ExecuteAsync(alice);
            var groups = await service.LoadAllGroupsAsync();
            Assert.Equal(new List<int> { 1 }, groups[0].MemberContactIds);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task RemoveMemberAsync_Removes()
    {
        var (service, dir) = MakeService();
        try
        {
            await service.UpsertContactAsync(MakeContact("Alice"));
            var gid = await service.CreateGroupAsync("G");
            await service.AddMemberAsync(gid, 1);
            var vm = new GroupManagerViewModel(service);
            await vm.LoadAsync();
            vm.SelectedGroup = vm.Groups[0];
            var alice = vm.GroupMembers[0];
            await vm.RemoveMemberCommand.ExecuteAsync(alice);
            var groups = await service.LoadAllGroupsAsync();
            Assert.Empty(groups[0].MemberContactIds);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task CreateGroupAsync_FromVm_Adds()
    {
        var (service, dir) = MakeService();
        try
        {
            var vm = new GroupManagerViewModel(service);
            await vm.LoadAsync();
            vm.NewName = "Fresh";
            await vm.CreateGroupCommand.ExecuteAsync(null);
            var groups = await service.LoadAllGroupsAsync();
            Assert.Single(groups);
            Assert.Equal("Fresh", groups[0].Name);
        }
        finally { Cleanup(dir); }
    }
}
