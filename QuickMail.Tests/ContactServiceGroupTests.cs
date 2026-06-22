using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for the group-side of ContactService: CRUD, membership, missing-contact
/// handling, atomic write, and corrupt-file recovery.
/// </summary>
public class ContactServiceGroupTestsV2
{
    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), $"QM-ContactGroupTests-{Guid.NewGuid():N}");

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
    public async Task LoadAllGroupsAsync_ReturnsEmptyList_WhenNoFileExists()
    {
        var (service, dir) = MakeService();
        try
        {
            var groups = await service.LoadAllGroupsAsync();
            Assert.NotNull(groups);
            Assert.Empty(groups);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task LoadAllGroupsAsync_ReturnsGroupsSortedByLastUsedDescending()
    {
        var (service, dir) = MakeService();
        try
        {
            var a = await service.CreateGroupAsync("Zebra");
            var b = await service.CreateGroupAsync("Alpha");
            await Task.Delay(20, TestContext.Current.CancellationToken);
            await service.TouchGroupAsync(b);
            var groups = await service.LoadAllGroupsAsync();
            Assert.Equal(2, groups.Count);
            Assert.Equal("Alpha", groups[0].Name);
            Assert.Equal("Zebra", groups[1].Name);
            Assert.NotEqual(a, b);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task CreateGroupAsync_AssignsIncrementingId()
    {
        var (service, dir) = MakeService();
        try
        {
            var a = await service.CreateGroupAsync("First");
            var b = await service.CreateGroupAsync("Second");
            Assert.Equal(1, a);
            Assert.Equal(2, b);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task CreateGroupAsync_RejectsEmptyName()
    {
        var (service, dir) = MakeService();
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateGroupAsync(""));
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateGroupAsync("   "));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task CreateGroupAsync_TrimsName()
    {
        var (service, dir) = MakeService();
        try
        {
            var id = await service.CreateGroupAsync("  Spaced Out  ");
            var groups = await service.LoadAllGroupsAsync();
            Assert.Equal("Spaced Out", groups.First(g => g.Id == id).Name);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task RenameGroupAsync_UpdatesName()
    {
        var (service, dir) = MakeService();
        try
        {
            var id = await service.CreateGroupAsync("Old");
            await service.RenameGroupAsync(id, "New");
            var groups = await service.LoadAllGroupsAsync();
            Assert.Equal("New", groups.First(g => g.Id == id).Name);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task RenameGroupAsync_ThrowsForMissingId()
    {
        var (service, dir) = MakeService();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RenameGroupAsync(999, "X"));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task DeleteGroupAsync_RemovesGroup()
    {
        var (service, dir) = MakeService();
        try
        {
            var id = await service.CreateGroupAsync("Doomed");
            await service.DeleteGroupAsync(id);
            var groups = await service.LoadAllGroupsAsync();
            Assert.Empty(groups);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task DeleteGroupAsync_DoesNotDeleteContacts()
    {
        var (service, dir) = MakeService();
        try
        {
            await service.UpsertContactAsync(MakeContact("Alice"));
            await service.UpsertContactAsync(MakeContact("Bob"));
            var id = await service.CreateGroupAsync("Friends");
            await service.AddMemberAsync(id, 1);
            await service.AddMemberAsync(id, 2);
            await service.DeleteGroupAsync(id);
            var contacts = await service.LoadAllContactsAsync();
            Assert.Equal(2, contacts.Count);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task AddMemberAsync_IsIdempotent()
    {
        var (service, dir) = MakeService();
        try
        {
            await service.UpsertContactAsync(MakeContact("Alice"));
            var id = await service.CreateGroupAsync("Friends");
            await service.AddMemberAsync(id, 1);
            await service.AddMemberAsync(id, 1);
            var groups = await service.LoadAllGroupsAsync();
            var group = groups.First(g => g.Id == id);
            Assert.Equal(new List<int> { 1 }, group.MemberContactIds);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task RemoveMemberAsync_IsIdempotent()
    {
        var (service, dir) = MakeService();
        try
        {
            await service.UpsertContactAsync(MakeContact("Alice"));
            var id = await service.CreateGroupAsync("Friends");
            await service.AddMemberAsync(id, 1);
            await service.RemoveMemberAsync(id, 1);
            await service.RemoveMemberAsync(id, 1);
            var groups = await service.LoadAllGroupsAsync();
            var group = groups.First(g => g.Id == id);
            Assert.Empty(group.MemberContactIds);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task AddAndRemoveMembers_UpdatesList()
    {
        var (service, dir) = MakeService();
        try
        {
            await service.UpsertContactAsync(MakeContact("Alice"));
            await service.UpsertContactAsync(MakeContact("Bob"));
            await service.UpsertContactAsync(MakeContact("Carol"));
            var id = await service.CreateGroupAsync("Friends");
            await service.AddMemberAsync(id, 1);
            await service.AddMemberAsync(id, 2);
            await service.AddMemberAsync(id, 3);
            await service.RemoveMemberAsync(id, 2);
            var groups = await service.LoadAllGroupsAsync();
            var group = groups.First(g => g.Id == id);
            Assert.Equal(new List<int> { 1, 3 }, group.MemberContactIds);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task ListGroupsForContactAsync_ReturnsAllGroupsContainingContact()
    {
        var (service, dir) = MakeService();
        try
        {
            await service.UpsertContactAsync(MakeContact("Alice"));
            await service.UpsertContactAsync(MakeContact("Bob"));
            var g1 = await service.CreateGroupAsync("Group1");
            var g2 = await service.CreateGroupAsync("Group2");
            var g3 = await service.CreateGroupAsync("Group3");
            await service.AddMemberAsync(g1, 1);
            await service.AddMemberAsync(g2, 1);
            await service.AddMemberAsync(g3, 2);
            var aliceGroups = await service.ListGroupsForContactAsync(1);
            var bobGroups   = await service.ListGroupsForContactAsync(2);
            Assert.Equal(new List<int> { g1, g2 }, aliceGroups);
            Assert.Equal(new List<int> { g3 }, bobGroups);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task LoadAllGroupsAsync_SkipsMissingContacts()
    {
        var (service, dir) = MakeService();
        try
        {
            await service.UpsertContactAsync(MakeContact("Alice"));
            var id = await service.CreateGroupAsync("Mixed");
            await service.AddMemberAsync(id, 1);
            await service.AddMemberAsync(id, 999);
            await service.AddMemberAsync(id, 998);
            var groups = await service.LoadAllGroupsAsync();
            var group  = groups.First(g => g.Id == id);
            Assert.Equal(1, group.ResolvedMemberCount);
            Assert.Equal(2, group.MissingContactCount);
            Assert.Contains("(2 missing)", group.Display);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task DisplayString_FormatsAsExpected()
    {
        var (service, dir) = MakeService();
        try
        {
            var empty = await service.CreateGroupAsync("Empty");
            var e = (await service.LoadAllGroupsAsync()).First(g => g.Id == empty);
            Assert.Equal("Empty group", e.Display);

            await service.UpsertContactAsync(MakeContact("Alice"));
            var one = await service.CreateGroupAsync("One");
            await service.AddMemberAsync(one, 1);
            var o = (await service.LoadAllGroupsAsync()).First(g => g.Id == one);
            Assert.Equal("One, 1 member", o.Display);

            var many = await service.CreateGroupAsync("Many");
            await service.UpsertContactAsync(MakeContact("Bob"));
            await service.AddMemberAsync(many, 1);
            await service.AddMemberAsync(many, 2);
            var m = (await service.LoadAllGroupsAsync()).First(g => g.Id == many);
            Assert.Equal("Many, 2 members", m.Display);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task LoadAllGroupsAsync_RecoversFromCorruptFile()
    {
        var dir = TempDir();
        try
        {
            var profile = new ProfileContext(dir);
            var service = new ContactService(profile);
            await service.CreateGroupAsync("Test");
            var groupsFile = Path.Combine(dir, "groups.json");
            File.WriteAllText(groupsFile, "{ this is not valid JSON");
            var service2 = new ContactService(profile);
            var groups = await service2.LoadAllGroupsAsync();
            Assert.Empty(groups);
            var bak = Directory.GetFiles(dir, "groups.json.bak-*");
            Assert.NotEmpty(bak);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task Groups_AreWrittenAtomically()
    {
        var (service, dir) = MakeService();
        try
        {
            await service.CreateGroupAsync("Atomic");
            var json = await File.ReadAllTextAsync(Path.Combine(dir, "groups.json"), TestContext.Current.CancellationToken);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(1, doc.RootElement.GetArrayLength());
            Assert.Empty(Directory.GetFiles(dir, "groups.json.tmp"));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task Concurrent_GroupAndContactWritesDoNotDeadlock()
    {
        var (service, dir) = MakeService();
        try
        {
            await service.UpsertContactAsync(MakeContact("Alice"));
            var groupId = await service.CreateGroupAsync("Concurrent");
            var tasks = new List<Task>();
            for (var i = 0; i < 50; i++)
            {
                tasks.Add(service.AddMemberAsync(groupId, 1));
                tasks.Add(service.LoadAllGroupsAsync());
                tasks.Add(service.UpsertContactAsync(MakeContact($"User{i}")));
            }
            var all = Task.WhenAll(tasks);
            var completed = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Same(all, completed);
        }
        finally { Cleanup(dir); }
    }
}
