using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using QuickMail.Fixtures;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// #180 Phase 1 round-trip: the fixture generator writes through the app's real
/// persistence services, and the same services read back exactly the curated
/// dataset. Because both sides are the shipping code, a schema migration that
/// desyncs the fixtures fails here, not at probe time.
/// </summary>
public class QuickMailFixturesTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"QM-FixtureTests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp cleanup */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task DefaultSet_RoundTripsThroughTheRealServices()
    {
        await DefaultFixtureSet.WriteAsync(_dir);
        var profile = new ProfileContext(_dir);

        // Account
        var accounts = new AccountService(profile).LoadAccounts();
        var account = Assert.Single(accounts);
        Assert.Equal(UiProbeFixture.AccountId, account.Id);
        Assert.True(account.IsDefault);
        Assert.Equal("test@example.com", account.Username);

        // Mail
        var store = new LocalStoreService(profile);
        store.Initialize();
        var all = await store.LoadAllSummariesAsync();
        Assert.Equal(9, all.Count);

        var inbox = await store.LoadFolderSummariesAsync(UiProbeFixture.AccountId, UiProbeFixture.InboxFolder);
        Assert.Equal(7, inbox.Count);
        Assert.Contains(inbox, m => !m.IsRead);                       // unread rows exist
        Assert.Contains(inbox, m => m.FlagId != null);                // the flagged row persisted a flag id
        Assert.Contains(inbox, m => m.IsMailingList);
        Assert.Contains(inbox, m => m.Subject.Length > 120);          // the pathological subject

        // The attachment indicator comes from UpsertDetailAsync, not the summary.
        var withAttachment = inbox.Single(m => m.MessageId == "1004");
        Assert.True(withAttachment.HasAttachments);

        // Reading-pane target: HTML body present and non-trivial.
        var html = await store.LoadDetailAsync(UiProbeFixture.AccountId, UiProbeFixture.InboxFolder, UiProbeFixture.HtmlMessageId);
        Assert.NotNull(html);
        Assert.Contains("<h1>", html!.HtmlBody, StringComparison.OrdinalIgnoreCase);

        // Invite: the reading pane's event card needs CalendarIcs on the detail.
        var invite = await store.LoadDetailAsync(UiProbeFixture.AccountId, UiProbeFixture.InboxFolder, "1007");
        Assert.NotNull(invite);
        Assert.Contains("BEGIN:VEVENT", invite!.CalendarIcs, StringComparison.Ordinal);

        // Config: first-run tutorial suppressed so probe shots show the app.
        var cfg = new ConfigService(profile).Load();
        Assert.True(cfg.TutorialCompleted);
        Assert.False(cfg.CloseToTray);

        // Supporting files exist and are non-empty.
        foreach (var file in new[] { "contacts.json", "groups.json", "flags.json", "views.json", "rules.json", "templates.json" })
            Assert.True(new FileInfo(Path.Combine(_dir, file)).Length > 0, $"{file} missing or empty");
    }

    [Fact]
    public async Task DefaultSet_IsDeterministic_AcrossRuns()
    {
        var dirA = _dir + "-a";
        var dirB = _dir + "-b";
        try
        {
            await DefaultFixtureSet.WriteAsync(dirA);
            await DefaultFixtureSet.WriteAsync(dirB);

            // JSON artifacts must be byte-identical (fixed GUIDs, fixed clock).
            // mail.db is excluded: SQLite internals (page layout) are not
            // byte-stable, but the row content is covered by the round-trip test.
            foreach (var file in new[] { "accounts.json", "contacts.json", "groups.json", "flags.json", "views.json", "rules.json", "templates.json" })
            {
                Assert.True(
                    File.ReadAllBytes(Path.Combine(dirA, file)).SequenceEqual(File.ReadAllBytes(Path.Combine(dirB, file))),
                    $"{file} differs between two generator runs — non-determinism crept in");
            }
        }
        finally
        {
            try { Directory.Delete(dirA, recursive: true); } catch { }
            try { Directory.Delete(dirB, recursive: true); } catch { }
        }
    }
}

/// <summary>--ui-probe flag parsing (#180 Phase 2).</summary>
public class UiProbeOptionsTests
{
    [Fact]
    public void Absent_ReturnsNullWithoutError()
    {
        var options = UiProbeOptions.Parse(["--profileDir", "x"], out var error);
        Assert.Null(options);
        Assert.Null(error);
    }

    [Fact]
    public void FullInvocation_ParsesEveryKnob()
    {
        var options = UiProbeOptions.Parse(
            ["--ui-probe", "inbox;Reading-Pane", "--theme", "dark", "--text-scale", "150",
             "--capture-dir", @"C:\shots", "--capture-tag", "01-inbox"], out var error);

        Assert.Null(error);
        Assert.NotNull(options);
        Assert.Equal(["inbox", "reading-pane"], options!.Surfaces);
        Assert.Equal("dark", options.ThemeId);
        Assert.Equal(1.5, options.TextScale);
        Assert.Equal(@"C:\shots", options.CaptureDir);
        Assert.Equal("01-inbox", options.CaptureTag);
    }

    [Fact]
    public void MissingSurface_IsAHardError()
    {
        var options = UiProbeOptions.Parse(["--ui-probe", "--theme", "dark"], out var error);
        Assert.Null(options);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("99")]
    [InlineData("201")]
    [InlineData("large")]
    public void BadScale_IsAHardError(string scale)
    {
        var options = UiProbeOptions.Parse(["--ui-probe", "inbox", "--text-scale", scale], out var error);
        Assert.Null(options);
        Assert.NotNull(error);
    }
}
