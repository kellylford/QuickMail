// A real LocalDraftService over a real SQLite file, for tests that would otherwise use an
// in-memory fake (#637).
//
// Five review rounds on this feature each found a defect the suite had passed, and each time the
// reason was the same: the fake was exactly one dimension less faithful than the store, along the
// axis the bug lived on. Round 3 it was the folder — the fake keyed rows by message id alone, so
// a message "moving" from Drafts to the Outbox was invisible. That was fixed by adding the folder
// to the key, with a comment saying it was not pedantry. Round 5 it was the account: the same fake
// ignored accountId entirely, so a sender switch that split the row key from its owner — and sent
// one message twice, from two different addresses — could not be represented at all.
//
// Patching the fake once per round loses that race by construction. The store keys rows on
// (unique_id, account_id, folder_name) and enforces the flag invariants in SQL; anything that
// paraphrases it will eventually paraphrase it wrongly. So tests that care about identity or
// persistence use this, and the fakes are kept only where a test genuinely needs to script a
// failure the real store cannot produce on demand.

using System;
using System.IO;
using QuickMail.Services;

namespace QuickMail.Tests;

/// <summary>A throwaway profile directory holding one real store, disposed with the test.</summary>
sealed class RealDraftStore : IDisposable
{
    private readonly string _dir;

    public RealDraftStore()
    {
        _dir  = Path.Combine(Path.GetTempPath(), $"QuickMailTests-{Guid.NewGuid():N}");
        Store = new LocalStoreService(new ProfileContext(_dir));
        Store.Initialize();
        Drafts = new LocalDraftService(Store);
    }

    public LocalStoreService Store { get; }
    public LocalDraftService Drafts { get; }

    /// <summary>A second service over the same file — what a relaunch looks like.</summary>
    public LocalStoreService Reopen()
    {
        var reopened = new LocalStoreService(new ProfileContext(_dir));
        reopened.Initialize();
        return reopened;
    }

    /// <summary>Seeds the folder cache so ResolveDraftsFolderNameAsync finds a Drafts folder.</summary>
    public async System.Threading.Tasks.Task SeedDraftsFolderAsync(Guid accountId, string name = "Drafts")
        => await Store.SaveFoldersAsync(accountId,
            [new Models.MailFolderModel
            {
                AccountId = accountId, FullName = name, DisplayName = name,
                Kind = Models.SpecialFolderKind.Drafts,
            }]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp dir; best effort */ }
    }
}
