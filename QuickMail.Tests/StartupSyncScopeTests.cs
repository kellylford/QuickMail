// How much QuickMail syncs at launch — issue #516.
//
// SyncAllAccountsAsync has exactly one caller, the startup pass, so it IS the startup sync and reads
// the scope from config itself. These tests drive it against a mail service that records which
// folders it was asked to fetch, because "which folders did we touch" is the whole behaviour.
//
// Worth keeping in mind while reading: nothing skipped here is skipped permanently. The periodic
// sweep visits every folder, and the IMAP IDLE / Graph delta watchers cover every account's Inbox
// live, so new-mail notifications are unaffected by any scope.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

public class StartupSyncScopeTests : IDisposable
{
    private static readonly Guid WorkId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid HomeId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly string _tempDir;
    private readonly LocalStoreService _store;

    public StartupSyncScopeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"qm-startup-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new LocalStoreService(new ProfileContext(_tempDir));
        _store.Initialize();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Records every (account, folder) the sync actually reached.</summary>
    private sealed class RecordingMailService : StubImapMailServiceBase
    {
        public List<(Guid Account, string Folder)> Fetched { get; } = [];

        public override Task<List<MailMessageSummary>> GetMessagesSinceDateAsync(
            Guid accountId, string folderName, DateTime since, CancellationToken ct = default)
        {
            Fetched.Add((accountId, folderName));
            return Task.FromResult(new List<MailMessageSummary>());
        }

        public override Task<List<MailMessageSummary>> GetMessagesSinceAsync(
            Guid accountId, string folderName, string sinceMessageId, int initialCount, CancellationToken ct = default)
        {
            Fetched.Add((accountId, folderName));
            return Task.FromResult(new List<MailMessageSummary>());
        }
    }

    private static MailFolderModel Folder(string full, string display, SpecialFolderKind kind = SpecialFolderKind.None) =>
        new() { FullName = full, DisplayName = display, Kind = kind };

    private static readonly List<MailFolderModel> WorkFolders =
    [
        Folder("INBOX", "Inbox", SpecialFolderKind.Inbox),
        Folder("INBOX/Projects", "Projects"),
        Folder("Archive", "Archive", SpecialFolderKind.Archive),
        Folder("Sent", "Sent", SpecialFolderKind.Sent),          // ExcludeFromAllMail is false here
    ];

    private static readonly List<MailFolderModel> HomeFolders =
    [
        Folder("INBOX", "Inbox", SpecialFolderKind.Inbox),
        Folder("Lists", "Lists"),
    ];

    private static readonly Dictionary<Guid, List<MailFolderModel>> Cached = new()
    {
        [WorkId] = WorkFolders,
        [HomeId] = HomeFolders,
    };

    private static readonly List<AccountModel> Accounts =
    [
        new() { Id = WorkId, AccountName = "Work", ImapHost = "work.example" },
        new() { Id = HomeId, AccountName = "Home", ImapHost = "home.example" },
    ];

    private async Task<List<(Guid Account, string Folder)>> RunAsync(Action<ConfigModel> configure)
    {
        var config = new StubConfigService();
        var cfg = config.Load();
        configure(cfg);
        config.Save(cfg);

        var imap = new RecordingMailService();
        var sync = new SyncService(imap, _store, config, new StubRuleService());

        await sync.SyncAllAccountsAsync(Accounts, Cached, CancellationToken.None);
        return imap.Fetched;
    }

    [Fact]
    public async Task ScopeAll_SyncsEveryNonExcludedFolderOnEveryAccount()
    {
        var fetched = await RunAsync(c => c.StartupSyncScope = ConfigModel.StartupSyncScopeAll);

        Assert.Equal(6, fetched.Count);
        Assert.Contains((WorkId, "INBOX/Projects"), fetched);
        Assert.Contains((HomeId, "Lists"), fetched);
    }

    [Fact]
    public async Task ScopeInboxes_SyncsOnlyInboxes_AcrossEveryAccount()
    {
        var fetched = await RunAsync(c => c.StartupSyncScope = ConfigModel.StartupSyncScopeInboxes);

        Assert.Equal([(WorkId, "INBOX"), (HomeId, "INBOX")], fetched.OrderBy(f => f.Account).ToList());
    }

    [Fact]
    public async Task ScopeStartupFolder_WithARealFolder_SyncsThatFolderAlone()
    {
        // The lightest launch there is, and the case the default exists for.
        var fetched = await RunAsync(c =>
        {
            c.StartupSyncScope       = ConfigModel.StartupSyncScopeStartupFolder;
            c.StartupFolder          = "INBOX/Projects";
            c.StartupFolderAccount   = WorkId.ToString();
        });

        Assert.Equal([(WorkId, "INBOX/Projects")], fetched);
    }

    [Fact]
    public async Task ScopeStartupFolder_WithAllInboxes_SyncsEveryInbox()
    {
        var fetched = await RunAsync(c =>
        {
            c.StartupSyncScope = ConfigModel.StartupSyncScopeStartupFolder;
            c.StartupFolder    = "AllInboxes";
        });

        Assert.Equal([(WorkId, "INBOX"), (HomeId, "INBOX")], fetched.OrderBy(f => f.Account).ToList());
    }

    [Fact]
    public async Task ScopeStartupFolder_WithNoStartupFolder_SyncsEverything()
    {
        // No startup folder means All Mail, which spans every folder — a narrower sync would put
        // stale rows on screen. So the saving is opted into by choosing a narrower place to start,
        // not imposed on someone who never configured one.
        var fetched = await RunAsync(c => c.StartupSyncScope = ConfigModel.StartupSyncScopeStartupFolder);

        Assert.Equal(6, fetched.Count);
    }

    [Fact]
    public async Task ScopeStartupFolder_WithAllMail_SyncsEverything()
    {
        var fetched = await RunAsync(c =>
        {
            c.StartupSyncScope = ConfigModel.StartupSyncScopeStartupFolder;
            c.StartupFolder    = "AllMail";
        });

        Assert.Equal(6, fetched.Count);
    }

    [Fact]
    public async Task ScopeStartupFolder_WhoseFolderNoLongerExists_SyncsWide_NotNothing()
    {
        // Startup falls back to All Mail in this case, so the sync has to cover All Mail. Syncing
        // one folder that is not there would leave the user looking at an unsynced All Mail.
        var fetched = await RunAsync(c =>
        {
            c.StartupSyncScope     = ConfigModel.StartupSyncScopeStartupFolder;
            c.StartupFolder        = "INBOX/GoneAway";
            c.StartupFolderAccount = WorkId.ToString();
        });

        Assert.Equal(6, fetched.Count);
    }

    [Fact]
    public async Task ScopeStartupFolder_WithAViewReference_SyncsEverything()
    {
        // This layer has no view service to resolve view:{guid}, and guessing narrow would under-sync.
        var fetched = await RunAsync(c =>
        {
            c.StartupSyncScope = ConfigModel.StartupSyncScopeStartupFolder;
            c.StartupFolder    = "view:cccccccc-cccc-cccc-cccc-cccccccccccc";
        });

        Assert.Equal(6, fetched.Count);
    }

    [Fact]
    public async Task ProgressTotal_CountsOnlyTheFoldersInScope()
    {
        // Otherwise "Synced 2 of 6 folders" announces a total the sync never intends to reach, and
        // the completion announcement never fires.
        var config = new StubConfigService();
        var cfg = config.Load();
        cfg.StartupSyncScope = ConfigModel.StartupSyncScopeInboxes;
        config.Save(cfg);

        var sync = new SyncService(new RecordingMailService(), _store, config, new StubRuleService());
        var progress = new List<(int Done, int Total)>();
        sync.SyncProgressChanged += (done, total) => progress.Add((done, total));

        await sync.SyncAllAccountsAsync(Accounts, Cached, CancellationToken.None);

        Assert.NotEmpty(progress);
        Assert.All(progress, p => Assert.Equal(2, p.Total));
        Assert.Equal(2, progress[^1].Done);
    }
}
