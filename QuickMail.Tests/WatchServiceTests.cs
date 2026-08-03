// Tests for WatchService — the store behind watched conversations.
//
// The defining property of a watch is that it is a *predicate*, not a mark: it is stored as a
// normalized-subject key, so a reply that has not arrived yet already matches. These tests pin the
// key normalization, the persistence round trip, and the blank-subject refusal (an empty key would
// match every blank-subject message in every account, which reads as the feature being broken).

using System;
using System.IO;
using System.Linq;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

public class WatchServiceTests
{
    private static ProfileContext MakeTempProfile() =>
        new(Path.Combine(Path.GetTempPath(), $"QM-WATCH-{Guid.NewGuid():N}"));

    // ── Matching ─────────────────────────────────────────────────────────────

    [Fact]
    public void IsWatched_MatchesAReplyToTheWatchedMessage()
    {
        // The whole point: watch the announcement, and the replies that arrive later are members
        // without anyone marking them.
        var svc = new WatchService(MakeTempProfile());
        Assert.True(svc.Watch("QuickMail 1.4 released"));

        Assert.True(svc.IsWatched("Re: QuickMail 1.4 released"));
        Assert.True(svc.IsWatched("Re: Re: Fwd: QuickMail 1.4 released"));
        Assert.True(svc.IsWatched("QuickMail 1.4 released"));
    }

    [Fact]
    public void IsWatched_IsCaseInsensitive()
    {
        var svc = new WatchService(MakeTempProfile());
        svc.Watch("Budget Review");

        Assert.True(svc.IsWatched("BUDGET REVIEW"));
        Assert.True(svc.IsWatched("re: budget review"));
    }

    [Fact]
    public void IsWatched_DoesNotMatchADifferentConversation()
    {
        var svc = new WatchService(MakeTempProfile());
        svc.Watch("Budget Review");

        Assert.False(svc.IsWatched("Budget Review 2027"));
        Assert.False(svc.IsWatched("Something else"));
    }

    [Fact]
    public void IsWatched_IsFalseForABlankSubject_EvenWithWatchesStored()
    {
        // Guards the trap this design exists to avoid: a stored empty key would make every
        // subjectless message a member of the watched folder.
        var svc = new WatchService(MakeTempProfile());
        svc.Watch("Budget Review");

        Assert.False(svc.IsWatched(""));
        Assert.False(svc.IsWatched("   "));
        Assert.False(svc.IsWatched("Re:"));
    }

    // ── Watch / Unwatch ──────────────────────────────────────────────────────

    [Fact]
    public void Watch_StoresTheNormalizedKeyAndTheOriginalSubjectAsTheLabel()
    {
        var svc = new WatchService(MakeTempProfile());
        svc.Watch("Re: Fwd: Budget Review");

        var entry = Assert.Single(svc.GetAll());
        Assert.Equal("Budget Review", entry.NormalizedSubject);
        Assert.Equal("Re: Fwd: Budget Review", entry.Label);
    }

    [Fact]
    public void Watch_RefusesABlankSubjectAndStoresNothing()
    {
        var svc = new WatchService(MakeTempProfile());

        Assert.False(svc.Watch(""));
        Assert.False(svc.Watch("   "));
        Assert.False(svc.Watch("Re: "));      // normalizes to empty
        Assert.Empty(svc.GetAll());
    }

    [Fact]
    public void Watch_OnAnAlreadyWatchedConversation_ReturnsFalseAndDoesNotDuplicate()
    {
        var svc = new WatchService(MakeTempProfile());
        Assert.True(svc.Watch("Budget Review"));

        // A different message of the same conversation is the same conversation.
        Assert.False(svc.Watch("Re: Budget Review"));
        Assert.False(svc.Watch("BUDGET REVIEW"));
        Assert.Single(svc.GetAll());
    }

    [Fact]
    public void Unwatch_WorksFromAnyMessageOfTheConversation()
    {
        var svc = new WatchService(MakeTempProfile());
        svc.Watch("Budget Review");

        Assert.True(svc.Unwatch("Re: Fwd: budget review"));
        Assert.Empty(svc.GetAll());
        Assert.False(svc.IsWatched("Budget Review"));
    }

    [Fact]
    public void Unwatch_ReturnsFalseWhenNotWatched()
    {
        var svc = new WatchService(MakeTempProfile());
        svc.Watch("Budget Review");

        Assert.False(svc.Unwatch("Some other thread"));
        Assert.False(svc.Unwatch(""));
        Assert.Single(svc.GetAll());
    }

    // ── Events ───────────────────────────────────────────────────────────────

    [Fact]
    public void WatchesChanged_FiresOnlyWhenTheStoredSetActuallyChanged()
    {
        var svc = new WatchService(MakeTempProfile());
        var fired = 0;
        svc.WatchesChanged += (_, _) => fired++;

        svc.Watch("Budget Review");   // changed
        svc.Watch("Re: Budget Review");   // already watched — no change
        svc.Watch("");                    // refused — no change
        svc.Unwatch("Nothing here");      // not watched — no change
        svc.Unwatch("Budget Review");     // changed

        Assert.Equal(2, fired);
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    [Fact]
    public void Watches_RoundTripThroughWatchesJson()
    {
        var profile = MakeTempProfile();
        var first = new WatchService(profile);
        first.Watch("Budget Review");
        first.Watch("Trip to Dublin");

        var reloaded = new WatchService(profile);

        Assert.Equal(2, reloaded.GetAll().Count);
        Assert.True(reloaded.IsWatched("Re: Budget Review"));
        Assert.True(reloaded.IsWatched("Fwd: Trip to Dublin"));
        Assert.True(File.Exists(Path.Combine(profile.ProfileDir, "watches.json")));
    }

    [Fact]
    public void Unwatch_IsPersisted()
    {
        var profile = MakeTempProfile();
        var first = new WatchService(profile);
        first.Watch("Budget Review");
        first.Watch("Trip to Dublin");
        first.Unwatch("Budget Review");

        var reloaded = new WatchService(profile);

        Assert.False(reloaded.IsWatched("Budget Review"));
        Assert.True(reloaded.IsWatched("Trip to Dublin"));
    }

    [Fact]
    public void CorruptWatchesFile_LoadsAsEmptyRatherThanThrowing()
    {
        // Same contract as ViewService: a bad file must never stop the app starting.
        var profile = MakeTempProfile();
        Directory.CreateDirectory(profile.ProfileDir);
        File.WriteAllText(Path.Combine(profile.ProfileDir, "watches.json"), "{ this is not json");

        var svc = new WatchService(profile);

        Assert.Empty(svc.GetAll());
        Assert.False(svc.IsWatched("anything"));
    }

    [Fact]
    public void HandEditedFile_NormalizesKeysAndDropsBlankOnes()
    {
        // watches.json is human-readable, so it will be hand-edited. A key written with a "Re:"
        // prefix must still match, and a blank key must never reach the match index.
        var profile = MakeTempProfile();
        Directory.CreateDirectory(profile.ProfileDir);
        File.WriteAllText(Path.Combine(profile.ProfileDir, "watches.json"),
            """
            [
              { "Id": "11111111-1111-1111-1111-111111111111",
                "NormalizedSubject": "Re: Budget Review", "Label": "Budget Review" },
              { "Id": "22222222-2222-2222-2222-222222222222",
                "NormalizedSubject": "", "Label": "junk" }
            ]
            """);

        var svc = new WatchService(profile);

        var entry = Assert.Single(svc.GetAll());
        Assert.Equal("Budget Review", entry.NormalizedSubject);
        Assert.True(svc.IsWatched("Budget Review"));
        Assert.False(svc.IsWatched(""));
    }

    [Fact]
    public void GetAll_ReturnsNewestFirst()
    {
        var svc = new WatchService(MakeTempProfile());
        svc.Watch("First");
        svc.Watch("Second");

        Assert.Equal("Second", svc.GetAll().First().NormalizedSubject);
    }
}
