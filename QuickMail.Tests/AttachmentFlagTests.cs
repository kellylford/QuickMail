// The attachment flag on the message list (#931). IMAP summaries now carry BODYSTRUCTURE and decide the flag by the
// same rule the attachment list uses; IMAP rows cached before that are checked once, after the startup sync, by a
// pass that asks the server for BODYSTRUCTURE alone and records the answer.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using Microsoft.Data.Sqlite;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class ImapAttachmentFlagRuleTests
{
    private static BodyPartText Text(string subtype) => new()
    {
        ContentType = new ContentType("text", subtype),
        PartSpecifier = "1",
    };

    private static BodyPartBasic Attached(string name) => new()
    {
        ContentType = new ContentType("application", "pdf"),
        ContentDisposition = new ContentDisposition("attachment") { FileName = name },
        PartSpecifier = "2",
    };

    private static BodyPartMultipart Multi(string subtype, params BodyPart[] parts)
    {
        var multi = new BodyPartMultipart { ContentType = new ContentType("multipart", subtype) };
        foreach (var part in parts) multi.BodyParts.Add(part);
        return multi;
    }

    [Fact]
    public void NoBodyStructureMeansNoAttachments() =>
        Assert.False(ImapMailService.HasAttachments(null));

    [Fact]
    public void APlainMessageHasNone() =>
        Assert.False(ImapMailService.HasAttachments(Multi("alternative", Text("plain"), Text("html"))));

    [Fact]
    public void AnAttachedFileCounts() =>
        Assert.True(ImapMailService.HasAttachments(Multi("mixed", Multi("alternative", Text("plain"), Text("html")), Attached("report.pdf"))));

    [Fact]
    public void AnInlinePictureInsideRelatedDoesNotCount()
    {
        var picture = new BodyPartBasic
        {
            ContentType = new ContentType("image", "png") { Name = "logo.png" },
            ContentId = "<logo>",
            PartSpecifier = "1.2",
        };
        Assert.False(ImapMailService.HasAttachments(Multi("related", Text("html"), picture)));
    }

    [Fact]
    public void ANamedFileWithNoDispositionCounts()
    {
        var part = new BodyPartBasic { ContentType = new ContentType("application", "zip") { Name = "a.zip" }, PartSpecifier = "2" };
        Assert.True(ImapMailService.HasAttachments(Multi("mixed", Text("plain"), part)));
    }

    [Fact]
    public void ASummaryWithNoBodyStructureIsNotKnown()
    {
        // Pins the guard below to the code that sets the flag: SummaryToModel has five callers, all from fetches that
        // ask for BODYSTRUCTURE. A new caller has to be checked against this test, and listed here.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "Services", "ImapMailService.cs"));
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(source, @"\bSummaryToModel\(s,").Count);
        Assert.Contains("AttachmentsKnown = s.Body != null", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EverySummaryFetchAsksForTheBodyStructure()
    {
        // A fetch that leaves BODYSTRUCTURE out makes every message it returns read as having no attachments, and
        // its upsert then records them as checked. Every SummaryToModel caller must ask for it.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "Services", "ImapMailService.cs"));
        var itemSets = System.Text.RegularExpressions.Regex.Matches(source,
            @"MessageSummaryItems\.UniqueId\s*\|\s*MessageSummaryItems\.Envelope\s*\|\s*MessageSummaryItems\.Flags\s*\|\s*MessageSummaryItems\.PreviewText[^;,]*[;,]");
        Assert.Equal(4, itemSets.Count);
        Assert.All(itemSets, m => Assert.Contains("MessageSummaryItems.BodyStructure", m.Value, StringComparison.Ordinal));
    }

    internal static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "QuickMail", "Views")))
                return dir.FullName;
        throw new InvalidOperationException("Repo source tree not found from " + AppContext.BaseDirectory + ".");
    }
}

public sealed class AttachmentFlagPassTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"qm-att-flags-{Guid.NewGuid():N}");
    private readonly LocalStoreService _store;
    private readonly StubConnectivityService _connectivity = new();

    public AttachmentFlagPassTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new LocalStoreService(new ProfileContext(_dir));
        _store.Initialize();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private sealed class ServerFlags : StubImapMailServiceBase, QuickMail.Services.IMailService
    {
        public HashSet<string> WithAttachments { get; } = [];
        public HashSet<string> OnServer { get; } = [];
        /// <summary>The Message-ID the server gives a message; none when not listed.</summary>
        public Dictionary<string, string> MessageIdOnServer { get; } = [];
        public List<(string Folder, List<string> Ids)> Fetches { get; } = [];
        public bool Unreachable { get; set; }
        public Dictionary<string, Exception> FailFolder { get; } = [];

        public Task<IReadOnlyDictionary<string, FetchedAttachmentFlag>> FetchAttachmentFlagsAsync(
            Guid accountId, string folderName, IList<string> messageIds, CancellationToken ct = default)
        {
            if (Unreachable) throw new SocketException((int)SocketError.HostUnreachable);
            if (FailFolder.TryGetValue(folderName, out var failure)) throw failure;
            Fetches.Add((folderName, messageIds.ToList()));
            return Task.FromResult<IReadOnlyDictionary<string, FetchedAttachmentFlag>>(
                messageIds.Where(OnServer.Contains).ToDictionary(id => id,
                    id => new FetchedAttachmentFlag(WithAttachments.Contains(id), MessageIdOnServer.GetValueOrDefault(id, ""))));
        }
    }

    private static AccountModel Account(BackendKind backend = BackendKind.ImapSmtp) => new()
    {
        Id = Guid.NewGuid(), AccountName = "Test", Username = "t@example.com", ImapHost = "host", BackendKind = backend,
    };

    private (SyncService Sync, ServerFlags Mail) Build()
    {
        var mail = new ServerFlags();
        var sync = new SyncService(mail, _store, new StubConfigService(), new StubRuleService(), connectivity: _connectivity)
        {
            AttachmentFlagPacing = TimeSpan.Zero,
        };
        return (sync, mail);
    }

    private Task Seed(AccountModel account, string id, string folder = "INBOX", bool hasAttachments = false,
                      string internetMessageId = "", bool known = true)
        => _store.UpsertSummariesAsync([new MailMessageSummary
        {
            MessageId = id, AccountId = account.Id, FolderName = folder, From = "a", Subject = id,
            Date = DateTimeOffset.UtcNow, HasAttachments = hasAttachments, InternetMessageId = internetMessageId,
            AttachmentsKnown = known,
        }]);

    /// <summary>What a row cached before #931 looks like: the column added with its default.</summary>
    private void MarkAllUnchecked()
    {
        using var conn = new SqliteConnection($"Data Source={Path.Combine(_dir, "mail.db")}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE MessageSummary SET att_checked = 0;";
        cmd.ExecuteNonQuery();
    }

    private async Task<bool> HasAttachments(AccountModel account, string id, string folder = "INBOX")
        => (await _store.LoadFolderSummariesAsync(account.Id, folder)).Single(m => m.MessageId == id).HasAttachments;

    [Fact]
    public async Task ARowASummaryWritesIsAlreadyChecked()
    {
        var imap = Account();
        await Seed(imap, "1");

        Assert.Empty(await _store.GetMessageIdsWithUncheckedAttachmentsAsync(imap.Id));
    }

    [Fact]
    public async Task OldRowsAreAskedAboutOnce_AndFlaggedWhereTheyHaveAttachments()
    {
        var imap = Account();
        await Seed(imap, "1");
        await Seed(imap, "2");
        await Seed(imap, "3", folder: "Archive");
        MarkAllUnchecked();
        var (sync, mail) = Build();
        mail.OnServer.UnionWith(["1", "2", "3"]);
        mail.WithAttachments.UnionWith(["2", "3"]);
        var raised = new List<(Guid, string, IReadOnlyCollection<string>)>();
        sync.AttachmentFlagsFound += (a, f, ids) => raised.Add((a, f, ids));

        await sync.BackfillAttachmentFlagsAsync([imap], CancellationToken.None);

        Assert.False(await HasAttachments(imap, "1"));
        Assert.True(await HasAttachments(imap, "2"));
        Assert.True(await HasAttachments(imap, "3", "Archive"));
        Assert.Empty(await _store.GetMessageIdsWithUncheckedAttachmentsAsync(imap.Id));
        Assert.Contains(raised, r => r.Item2 == "INBOX" && r.Item3.SequenceEqual(["2"]));

        mail.Fetches.Clear();
        await sync.BackfillAttachmentFlagsAsync([imap], CancellationToken.None);
        Assert.Empty(mail.Fetches);
    }

    [Fact]
    public async Task ASummaryWithNoBodyStructureIsLeftForThePass()
    {
        var imap = Account();
        await Seed(imap, "1", known: false);

        Assert.Single(await _store.GetMessageIdsWithUncheckedAttachmentsAsync(imap.Id));

        await Seed(imap, "1", known: true);   // a later fetch that knows settles it
        Assert.Empty(await _store.GetMessageIdsWithUncheckedAttachmentsAsync(imap.Id));
    }

    [Fact]
    public async Task ARenumberedFolderDoesNotFlagTheWrongMessage()
    {
        var imap = Account();
        await Seed(imap, "1", internetMessageId: "cached@x");
        await Seed(imap, "2", internetMessageId: "same@x");
        MarkAllUnchecked();
        var (sync, mail) = Build();
        mail.OnServer.UnionWith(["1", "2"]);
        mail.WithAttachments.UnionWith(["1", "2"]);
        mail.MessageIdOnServer["1"] = "another@x";   // the id now names another message
        mail.MessageIdOnServer["2"] = "same@x";

        await sync.BackfillAttachmentFlagsAsync([imap], CancellationToken.None);

        Assert.False(await HasAttachments(imap, "1"));
        Assert.True(await HasAttachments(imap, "2"));
        Assert.Empty(await _store.GetMessageIdsWithUncheckedAttachmentsAsync(imap.Id));
    }

    [Fact]
    public async Task IdsThatAreNotUidsAreRecordedWithoutAsking()
    {
        var imap = Account();
        await Seed(imap, "not-a-uid");
        MarkAllUnchecked();
        var (sync, mail) = Build();

        await sync.BackfillAttachmentFlagsAsync([imap], CancellationToken.None);

        Assert.Empty(mail.Fetches);
        Assert.Empty(await _store.GetMessageIdsWithUncheckedAttachmentsAsync(imap.Id));
    }

    [Fact]
    public async Task AFolderThatRefusesDoesNotBlockTheOthers()
    {
        var imap = Account();
        await Seed(imap, "1", folder: "Alpha");
        await Seed(imap, "2", folder: "Beta");
        MarkAllUnchecked();
        var (sync, mail) = Build();
        mail.FailFolder["Alpha"] = new MailKit.Net.Imap.ImapCommandException(
            MailKit.Net.Imap.ImapCommandResponse.No, "cannot select");
        mail.OnServer.Add("2");
        mail.WithAttachments.Add("2");

        await sync.BackfillAttachmentFlagsAsync([imap], CancellationToken.None);

        Assert.True(await HasAttachments(imap, "2", "Beta"));
        var left = await _store.GetMessageIdsWithUncheckedAttachmentsAsync(imap.Id);
        Assert.Equal(["Alpha"], left.Keys);   // retried next launch
    }

    [Fact]
    public async Task TheInboxIsCheckedFirst()
    {
        var imap = Account();
        await Seed(imap, "1", folder: "Archive");
        await Seed(imap, "2", folder: "INBOX");
        MarkAllUnchecked();

        var order = (await _store.GetMessageIdsWithUncheckedAttachmentsAsync(imap.Id)).Keys.ToList();

        Assert.Equal(["INBOX", "Archive"], order);
    }

    [Fact]
    public void RowsCachedBeforeTheColumnExistedStartUnchecked()
    {
        // An old mail.db: the store without att_checked, as every profile is before this version.
        var imap = Account();
        Seed(imap, "1").GetAwaiter().GetResult();
        using (var conn = new SqliteConnection($"Data Source={Path.Combine(_dir, "mail.db")}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP INDEX IF EXISTS idx_summary_att_unchecked; ALTER TABLE MessageSummary DROP COLUMN att_checked;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var reopened = new LocalStoreService(new ProfileContext(_dir));
        reopened.Initialize();

        Assert.Single(reopened.GetMessageIdsWithUncheckedAttachmentsAsync(imap.Id).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task AFlagAlreadySetIsNeverCleared()
    {
        var imap = Account();
        await Seed(imap, "1", hasAttachments: true);
        MarkAllUnchecked();
        var (sync, mail) = Build();
        mail.OnServer.Add("1");

        await sync.BackfillAttachmentFlagsAsync([imap], CancellationToken.None);

        Assert.True(await HasAttachments(imap, "1"));
    }

    [Fact]
    public async Task OnlyImapAccountsAreAsked()
    {
        var graph = Account(BackendKind.MicrosoftGraph);
        await Seed(graph, "g1");
        MarkAllUnchecked();
        var (sync, mail) = Build();

        await sync.BackfillAttachmentFlagsAsync([graph], CancellationToken.None);

        Assert.Empty(mail.Fetches);
    }

    [Fact]
    public async Task AnUnreachableServerLeavesItsRowsForTheNextLaunch()
    {
        var imap = Account();
        await Seed(imap, "1");
        MarkAllUnchecked();
        var (sync, mail) = Build();
        mail.Unreachable = true;

        await sync.BackfillAttachmentFlagsAsync([imap], CancellationToken.None);

        Assert.Single(await _store.GetMessageIdsWithUncheckedAttachmentsAsync(imap.Id));
    }

    [Fact]
    public async Task AnOfflineAccountIsNotAsked()
    {
        var imap = Account();
        await Seed(imap, "1");
        MarkAllUnchecked();
        _connectivity.SetAccount(imap.Id, online: false);
        var (sync, mail) = Build();

        await sync.BackfillAttachmentFlagsAsync([imap], CancellationToken.None);

        Assert.Empty(mail.Fetches);
        Assert.Single(await _store.GetMessageIdsWithUncheckedAttachmentsAsync(imap.Id));
    }

    [Fact]
    public async Task AFolderGoneFromTheServerIsNotAskedAboutAgain()
    {
        var imap = Account();
        await Seed(imap, "1", folder: "Gone");
        await Seed(imap, "2");
        MarkAllUnchecked();
        var (sync, mail) = Build();
        mail.FailFolder["Gone"] = new FolderNotFoundException("Gone");
        mail.OnServer.Add("2");
        mail.WithAttachments.Add("2");

        await sync.BackfillAttachmentFlagsAsync([imap], CancellationToken.None);

        Assert.Empty(await _store.GetMessageIdsWithUncheckedAttachmentsAsync(imap.Id));
        Assert.True(await HasAttachments(imap, "2"));
    }

    [Fact]
    public async Task ARefusalLeavesTheAccountForTheNextLaunch()
    {
        var imap = Account();
        await Seed(imap, "1");
        MarkAllUnchecked();
        var (sync, mail) = Build();
        mail.FailFolder["INBOX"] = new InvalidOperationException("NO [UNAVAILABLE] try later");

        await sync.BackfillAttachmentFlagsAsync([imap], CancellationToken.None);

        Assert.Single(await _store.GetMessageIdsWithUncheckedAttachmentsAsync(imap.Id));
    }
}

public class AttachmentFlagsFoundViewModelTests
{
    [Fact]
    public void RowsOnScreenAreFlaggedAtOnce()
    {
        var account = Guid.NewGuid();
        var inbox = new MailMessageSummary { MessageId = "1", AccountId = account, FolderName = "INBOX" };
        var other = new MailMessageSummary { MessageId = "1", AccountId = account, FolderName = "Archive" };
        Assert.Equal(1, MainViewModel.ApplyAttachmentFlags([inbox, other], account, "INBOX", ["1"]));
        Assert.Equal(0, MainViewModel.ApplyAttachmentFlags([inbox, other], account, "INBOX", ["1"]));

        Assert.True(inbox.HasAttachments);
        Assert.False(other.HasAttachments);
    }
}
