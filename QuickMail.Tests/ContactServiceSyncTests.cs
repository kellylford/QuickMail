using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for the server-contact-sync side of ContactService (issue #256): the wholesale
/// replace/remove merge and the read-time dedup-by-email. The invariant that matters most is that
/// a sync never touches local contacts or another account's synced contacts.
/// </summary>
public class ContactServiceSyncTests
{
    private static (ContactService service, string dir) MakeService()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"QM-ContactSyncTests-{Guid.NewGuid():N}");
        return (new ContactService(new ProfileContext(dir)), dir);
    }

    private static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    private static ContactModel Synced(string sourceId, string name, string email, bool prior = false) => new()
    {
        SourceId         = sourceId,
        DisplayName      = name,
        EmailAddress     = email,
        IsPriorRecipient = prior,
    };

    [Fact]
    public async Task Replace_PreservesLocalContacts()
    {
        var (svc, dir) = MakeService();
        var acct = Guid.NewGuid();
        try
        {
            await svc.UpsertContactAsync(new ContactModel { DisplayName = "Local Person", EmailAddress = "local@x.test" });

            await svc.ReplaceSyncedContactsAsync(acct, ContactSource.Microsoft,
                [Synced("m1", "Server Person", "server@x.test")]);

            var all = await svc.LoadAllContactsAsync();
            Assert.Contains(all, c => c.EmailAddress == "local@x.test" && c.IsLocal);
            Assert.Contains(all, c => c.EmailAddress == "server@x.test" && c.Source == ContactSource.Microsoft);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task Replace_RemovesStaleServerRows_ButKeepsUpdatedOnesWithSameId()
    {
        var (svc, dir) = MakeService();
        var acct = Guid.NewGuid();
        try
        {
            await svc.ReplaceSyncedContactsAsync(acct, ContactSource.Microsoft,
                [Synced("m1", "First", "one@x.test"), Synced("m2", "Second", "two@x.test")]);
            var firstPass = await svc.LoadAllContactsAsync();
            var m1Id = firstPass.Single(c => c.SourceId == "m1").Id;

            // Second sync: m1 renamed, m2 gone, m3 new.
            await svc.ReplaceSyncedContactsAsync(acct, ContactSource.Microsoft,
                [Synced("m1", "First Renamed", "one@x.test"), Synced("m3", "Third", "three@x.test")]);

            var all = await svc.LoadAllContactsAsync();
            Assert.DoesNotContain(all, c => c.SourceId == "m2");                       // stale removed
            Assert.Contains(all, c => c.SourceId == "m3");                             // new added
            var m1 = all.Single(c => c.SourceId == "m1");
            Assert.Equal("First Renamed", m1.DisplayName);                             // updated in place
            Assert.Equal(m1Id, m1.Id);                                                // stable id preserved
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task Replace_IgnoresOtherAccountsAndSources()
    {
        var (svc, dir) = MakeService();
        var acctA = Guid.NewGuid();
        var acctB = Guid.NewGuid();
        try
        {
            await svc.ReplaceSyncedContactsAsync(acctA, ContactSource.Microsoft, [Synced("a1", "A One", "a1@x.test")]);
            await svc.ReplaceSyncedContactsAsync(acctB, ContactSource.Google,    [Synced("b1", "B One", "b1@x.test")]);

            // Re-sync account A with an empty set — B's Google rows must survive.
            await svc.ReplaceSyncedContactsAsync(acctA, ContactSource.Microsoft, []);

            var all = await svc.LoadAllContactsAsync();
            Assert.DoesNotContain(all, c => c.SourceId == "a1");
            Assert.Contains(all, c => c.SourceId == "b1" && c.Source == ContactSource.Google);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task Replace_SkipsRowsWithNoEmailOrNoSourceId()
    {
        var (svc, dir) = MakeService();
        var acct = Guid.NewGuid();
        try
        {
            await svc.ReplaceSyncedContactsAsync(acct, ContactSource.Microsoft,
            [
                Synced("m1", "Good", "good@x.test"),
                Synced("", "No Id", "noid@x.test"),
                Synced("m2", "No Email", ""),
            ]);
            var all = await svc.LoadAllContactsAsync();
            Assert.Single(all);
            Assert.Equal("m1", all[0].SourceId);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task RemoveSynced_DropsOnlyThatAccountsRows()
    {
        var (svc, dir) = MakeService();
        var acct = Guid.NewGuid();
        try
        {
            await svc.UpsertContactAsync(new ContactModel { DisplayName = "Keep Me", EmailAddress = "keep@x.test" });
            await svc.ReplaceSyncedContactsAsync(acct, ContactSource.Microsoft, [Synced("m1", "Gone", "gone@x.test")]);

            await svc.RemoveSyncedContactsAsync(acct);

            var all = await svc.LoadAllContactsAsync();
            Assert.Single(all);
            Assert.Equal("keep@x.test", all[0].EmailAddress);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task Search_DedupsByEmail_PreferringLocalThenSavedThenPrior()
    {
        var (svc, dir) = MakeService();
        var acct = Guid.NewGuid();
        try
        {
            // Same address from three origins.
            await svc.UpsertContactAsync(new ContactModel { DisplayName = "Local Name", EmailAddress = "dup@x.test" });
            await svc.ReplaceSyncedContactsAsync(acct, ContactSource.Microsoft,
            [
                Synced("m1", "Saved Name", "dup@x.test"),
                Synced("p1", "Prior Name", "dup@x.test", prior: true),
            ]);

            var results = await svc.SearchContactsAsync("dup");
            Assert.Single(results);
            Assert.Equal("Local Name", results[0].DisplayName); // local wins
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task Search_DedupIsCaseInsensitiveOnEmail()
    {
        var (svc, dir) = MakeService();
        var acct = Guid.NewGuid();
        try
        {
            await svc.ReplaceSyncedContactsAsync(acct, ContactSource.Microsoft,
            [
                Synced("m1", "Saved", "Person@X.Test"),
                Synced("p1", "Prior", "person@x.test", prior: true),
            ]);
            var results = await svc.SearchContactsAsync("person");
            Assert.Single(results);
            Assert.Equal("Saved", results[0].DisplayName); // saved outranks prior recipient
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task OldContactsJson_WithoutProvenanceFields_LoadsAsLocal()
    {
        var (svc, dir) = MakeService();
        try
        {
            Directory.CreateDirectory(dir);
            // Legacy shape: only the four original fields, no Source/SourceId/OwnerAccountId.
            var legacy = "[{\"Id\":1,\"DisplayName\":\"Legacy\",\"EmailAddress\":\"legacy@x.test\",\"LastUsedTicks\":0}]";
            await File.WriteAllTextAsync(Path.Combine(dir, "contacts.json"), legacy);

            var all = await svc.LoadAllContactsAsync();
            Assert.Single(all);
            Assert.True(all[0].IsLocal);
            Assert.Equal(ContactSource.Local, all[0].Source);
            Assert.Null(all[0].OwnerAccountId);
        }
        finally { Cleanup(dir); }
    }
}
