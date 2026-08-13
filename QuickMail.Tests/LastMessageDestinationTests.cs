// Issue #515: the Move to Folder picker should open on the folder messages were last moved to.
//
// Filing is repetitive — a run of messages usually goes to the same place — and until now the
// picker opened on the folder they came out of (#490), so the walk to the destination was repeated
// for every message. The destination of the last successful move, and separately of the last copy,
// is remembered per account and becomes where the picker opens; when nothing is remembered, or
// what is remembered no longer exists, the old behaviour stands.
//
// Per account because destinations are: the backends move by folder NAME over the source account's
// connection, so one account's remembered folder is meaningless for another — and with a name
// collision ("Archive") it would point at the wrong mailbox.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class LastMessageDestinationTests
{
    private static readonly Guid WorkId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid HomeId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private sealed class AccountsStub(params AccountModel[] accounts) : IAccountService
    {
        public List<AccountModel> LoadAccounts() => [.. accounts];
        public void SaveAccounts(List<AccountModel> a) { }
        public void SetDefaultAccount(Guid accountId) { }
    }

    /// <summary>An IMAP stub whose move and copy can be made to fail, so the "only on success"
    /// rule can be tested rather than assumed.</summary>
    private sealed class FailableMailService : StubImapMailServiceBase
    {
        public bool Fail { get; set; }

        public override Task MoveMessagesAsync(Guid accountId, string folderName, IList<string> messageIds,
                                               string destinationFolder, CancellationToken ct = default)
            => Fail ? Task.FromException(new InvalidOperationException("server said no")) : Task.CompletedTask;

        public override Task CopyMessagesAsync(Guid accountId, string folderName, IList<string> messageIds,
                                               string destinationFolder, CancellationToken ct = default)
            => Fail ? Task.FromException(new InvalidOperationException("server said no")) : Task.CompletedTask;
    }

    private static MailFolderModel Folder(Guid accountId, string fullName) => new()
    {
        AccountId   = accountId,
        FullName    = fullName,
        DisplayName = fullName.Split('/')[^1],
    };

    private static MailMessageSummary Message(Guid accountId, string folder = "INBOX", string id = "1") => new()
    {
        MessageId  = id,
        AccountId  = accountId,
        FolderName = folder,
    };

    /// <summary>A VM with two accounts whose folder lists are already cached, as they are at
    /// startup once the persisted folder list has been restored (#516).</summary>
    private static async Task<(MainViewModel Vm, StubConfigService Config, FailableMailService Mail)> MakeVmAsync()
    {
        var config     = new StubConfigService();
        var (vm, mail) = MakeVmWith(config);
        await vm.InitialLoadAsync();
        return (vm, config, mail);
    }

    /// <summary>
    /// The same VM over a caller-supplied config service, so a test can hand it a real
    /// <see cref="ConfigService"/> and stand a second one up afterwards to play the next launch.
    /// The folder lists come from the local store, not the config, so a "restart" keeps them.
    /// </summary>
    private static (MainViewModel Vm, FailableMailService Mail) MakeVmWith(IConfigService config)
    {
        var mail  = new FailableMailService();
        var store = new StubLocalStoreService();
        store.SeededFolders[WorkId] =
        [
            Folder(WorkId, "INBOX"), Folder(WorkId, "Archive"), Folder(WorkId, "Projects/2026"),
        ];
        store.SeededFolders[HomeId] = [Folder(HomeId, "INBOX"), Folder(HomeId, "Archive")];

        var vm = new MainViewModel(
            mail,
            new AccountsStub(
                new AccountModel { Id = WorkId, AccountName = "Work" },
                new AccountModel { Id = HomeId, AccountName = "Home" }),
            new StubCredentialService(), store, new StubOAuthService(),
            new StubSyncService(), config, new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService());

        vm.LoadAccountList();
        return (vm, mail);
    }

    [Fact]
    public async Task NothingRemembered_YetGivesNoDestination()
    {
        // The first move of the session has nothing to go on; the caller falls back to the folder
        // the messages came from, which is what happened before #515.
        var (vm, _, _) = await MakeVmAsync();

        Assert.Null(vm.LastDestinationFor([Message(WorkId)], copy: false));
    }

    [Fact]
    public async Task AfterAMove_ThePickerOpensOnWhereItWent()
    {
        var (vm, config, _) = await MakeVmAsync();

        await vm.MoveSelectedMessagesToFolderAsync([Message(WorkId)], Folder(WorkId, "Projects/2026"));

        Assert.Equal("Projects/2026", config.Load().Accounts[WorkId].LastMoveFolder);
        Assert.Equal("Projects/2026", vm.LastDestinationFor([Message(WorkId)], copy: false)?.FullName);
    }

    [Fact]
    public async Task ItSurvivesARestart()
    {
        // The whole point is the second and third message, which may well be in another session.
        // A real ConfigService over a temp profile, not the in-memory stub: the stub hands back the
        // very object the VM just mutated, so asserting against it would prove only that a field
        // was set — the file is where the value has to land.
        var dir     = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"QM-LastDest-{Guid.NewGuid():N}");
        var profile = new ProfileContext(dir);
        try
        {
            var (vm, _) = MakeVmWith(new ConfigService(profile));
            await vm.InitialLoadAsync();

            await vm.MoveSelectedMessagesToFolderAsync([Message(WorkId)], Folder(WorkId, "Archive"));

            // A second service reading the file the first one wrote — the next launch.
            var reloaded = new ConfigService(profile).Load();
            Assert.Equal("Archive", reloaded.Accounts[WorkId].LastMoveFolder);

            // And the restarted app resolves it back to a folder, which is what the picker needs.
            var (restarted, _) = MakeVmWith(new ConfigService(profile));
            await restarted.InitialLoadAsync();
            Assert.Equal("Archive", restarted.LastDestinationFor([Message(WorkId)], copy: false)?.FullName);
        }
        finally { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task MoveAndCopyAreRememberedSeparately()
    {
        // Copying to a reference folder and filing to an archive are different habits; letting one
        // set the other's starting point would make both wrong half the time.
        var (vm, _, _) = await MakeVmAsync();

        await vm.MoveSelectedMessagesToFolderAsync([Message(WorkId)], Folder(WorkId, "Archive"));
        await vm.CopySelectedMessagesToFolderAsync([Message(WorkId)], Folder(WorkId, "Projects/2026"));

        Assert.Equal("Archive",       vm.LastDestinationFor([Message(WorkId)], copy: false)?.FullName);
        Assert.Equal("Projects/2026", vm.LastDestinationFor([Message(WorkId)], copy: true)?.FullName);
    }

    [Fact]
    public async Task EachAccountRemembersItsOwn()
    {
        var (vm, _, _) = await MakeVmAsync();

        await vm.MoveSelectedMessagesToFolderAsync([Message(WorkId)], Folder(WorkId, "Projects/2026"));

        Assert.Equal("Projects/2026", vm.LastDestinationFor([Message(WorkId)], copy: false)?.FullName);
        Assert.Null(vm.LastDestinationFor([Message(HomeId)], copy: false));
    }

    [Fact]
    public async Task ASelectionSpanningAccounts_GetsNoDestination()
    {
        // There is no one remembered folder that is right for all of it, and the picker must not
        // open on a folder belonging to only one of the accounts in play.
        var (vm, _, _) = await MakeVmAsync();
        await vm.MoveSelectedMessagesToFolderAsync([Message(WorkId)], Folder(WorkId, "Archive"));

        var mixed = new[] { Message(WorkId, id: "1"), Message(HomeId, id: "2") };

        Assert.Null(vm.LastDestinationFor(mixed, copy: false));
    }

    [Fact]
    public async Task ASelectionSpanningFoldersOfOneAccount_StillGetsIt()
    {
        // Better than the folder-of-origin fallback, which needs a single source folder and gives
        // up here (MainWindow.CurrentFolderOf).
        var (vm, _, _) = await MakeVmAsync();
        await vm.MoveSelectedMessagesToFolderAsync([Message(WorkId)], Folder(WorkId, "Archive"));

        var spread = new[] { Message(WorkId, "INBOX", "1"), Message(WorkId, "Projects/2026", "2") };

        Assert.Equal("Archive", vm.LastDestinationFor(spread, copy: false)?.FullName);
    }

    [Fact]
    public async Task AFolderThatNoLongerExists_IsNotOffered()
    {
        // Deleted, renamed, or on an account that has been removed. Resolving against the folders
        // that exist now means the picker quietly goes back to opening where the messages are.
        var (vm, config, _) = await MakeVmAsync();
        var cfg = config.Load();
        cfg.Accounts[WorkId] = new AccountOverrideConfig { LastMoveFolder = "Projects/2019" };
        config.Save(cfg);

        Assert.Null(vm.LastDestinationFor([Message(WorkId)], copy: false));
    }

    [Fact]
    public async Task AFailedMove_IsNotRemembered()
    {
        // Nothing was filed there, so offering it next time would misreport what happened.
        var (vm, config, mail) = await MakeVmAsync();
        mail.Fail = true;

        await vm.MoveSelectedMessagesToFolderAsync([Message(WorkId)], Folder(WorkId, "Archive"));

        // Guards the guard: without this the test would pass just as well against a move that
        // silently succeeded and a feature that recorded nothing at all.
        Assert.Contains("Failed to move", vm.StatusText, StringComparison.Ordinal);
        Assert.False(config.Load().Accounts.TryGetValue(WorkId, out var ovr) &&
                     !string.IsNullOrEmpty(ovr.LastMoveFolder));
    }

    [Fact]
    public async Task AMoveToWhereTheMessagesAlreadyAre_IsNotRemembered()
    {
        // The guarded no-op case: it returns before touching the server, so there is nothing to
        // remember, and recording it would move the picker's opening folder for a move that never
        // happened.
        var (vm, config, _) = await MakeVmAsync();

        await vm.MoveSelectedMessagesToFolderAsync([Message(WorkId, "INBOX")], Folder(WorkId, "INBOX"));

        Assert.False(config.Load().Accounts.TryGetValue(WorkId, out var ovr) &&
                     !string.IsNullOrEmpty(ovr.LastMoveFolder));
    }

    [Theory]
    [InlineData(false)]   // \0AccountMail:{guid} — this account's "All Mail"
    [InlineData(true)]    // the top-level All Mail aggregate
    public async Task AVirtualFolder_IsNeverWrittenToConfig(bool topLevelAggregate)
    {
        // Not reachable through the picker, which does not offer these as destinations — but a
        // sentinel in config.ini is the shape of the saved-view corruption #520 was about, and the
        // entry point is where that is cheap to refuse.
        //
        // The per-account case is the one that matters and the one a hand-rolled "is the AccountId
        // empty" test lets straight through: an account's own All Mail, a saved view, and a
        // contact-mail result all carry a REAL AccountId and are told apart only by their sentinel
        // FullName. Removing the sentinel clause from the guard must fail a test here.
        var (vm, config, _) = await MakeVmAsync();
        var destination = topLevelAggregate
            ? MainViewModel.AllMailFolder
            : new MailFolderModel
            {
                AccountId   = WorkId,
                FullName    = MainViewModel.AccountMailPrefix + WorkId,
                DisplayName = "All Mail",
            };

        await vm.MoveSelectedMessagesToFolderAsync([Message(WorkId)], destination);

        // Nothing at all was written: the guard returns before touching the config.
        Assert.DoesNotContain(config.Load().Accounts.Values,
            o => !string.IsNullOrEmpty(o.LastMoveFolder) || !string.IsNullOrEmpty(o.LastCopyFolder));
        Assert.Null(vm.LastDestinationFor([Message(WorkId)], copy: false));
    }

    [Fact]
    public void TheConfigFileRoundTripsBothValues()
    {
        // Through a real ConfigService, so the [account:guid] section is genuinely written and read.
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"QM-LastDest-{Guid.NewGuid():N}");
        var profile = new ProfileContext(dir);
        try
        {
            var service = new ConfigService(profile);
            var cfg = service.Load();
            cfg.Accounts[WorkId] = new AccountOverrideConfig
            {
                LastMoveFolder = "Projects/2026",
                LastCopyFolder = "Reference",
            };
            service.Save(cfg);

            var reloaded = new ConfigService(profile).Load();

            Assert.Equal("Projects/2026", reloaded.Accounts[WorkId].LastMoveFolder);
            Assert.Equal("Reference",     reloaded.Accounts[WorkId].LastCopyFolder);
        }
        finally { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, recursive: true); }
    }
}

/// <summary>
/// The remembered destination is only useful if the picker is actually built with it. Every
/// message move and copy goes through <c>MainWindow.BuildMessageFolderPicker</c>, so that one
/// method is where the wiring can be lost — by dropping the <c>LastDestinationFor</c> call, or by
/// a new call site passing the wrong <c>copy:</c> value and quietly filing from the other memory.
///
/// <para>Neither can be exercised without a real <c>MainWindow</c> and a live
/// <c>MainViewModel</c>, so this reads the source, as <see cref="FolderMoveCopyCallSiteTests"/>
/// does for #431. Plain <c>[Fact]</c> — text and regex, no WPF — so it stays out of the
/// <c>WpfTests</c> collection.</para>
/// </summary>
public class LastMessageDestinationCallSiteTests
{
    [Fact]
    public void ThePickerIsBuiltWithTheRememberedDestination()
    {
        var body = MethodBody("Views/MainWindow.xaml.cs", "BuildMessageFolderPicker");

        Assert.Contains("LastDestinationFor", body, StringComparison.Ordinal);
        // The fallback must survive too: with nothing remembered the picker still opens on the
        // folder the messages came from (#490), never on nothing (#431).
        Assert.Contains("CurrentFolderOf", body, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryCallSitePassesTheIntentThatMatchesItsTitle()
    {
        // Every call, not a list of the titles that exist today: a new entry point with a new
        // title, or one that leaves the flag off, is exactly what this is here to catch, and a
        // fixed [InlineData] table would let both through by never looking at them.
        var source = File.ReadAllText(SourcePath("Views/MainWindow.xaml.cs"));
        var calls  = Regex.Matches(source, @"BuildMessageFolderPicker\(\s*[^;]*?""(?<title>[^""]*)""[^;]*?\)\s*;");

        Assert.Equal(10, calls.Count);   // update deliberately when an entry point is added or removed

        foreach (Match call in calls)
        {
            var title    = call.Groups["title"].Value;
            var isCopy   = title.StartsWith("Copy", StringComparison.Ordinal);
            var expected = isCopy ? "copy: true" : "copy: false";

            Assert.True(title.StartsWith("Move", StringComparison.Ordinal) || isCopy,
                        $"a picker titled \"{title}\" is neither a move nor a copy — which memory should it open from?");
            Assert.True(call.Value.Contains(expected, StringComparison.Ordinal),
                        $"the picker titled \"{title}\" does not pass {expected}, so it would open from the other memory.");
        }
    }

    private static string MethodBody(string relativePath, string methodName)
    {
        var source = File.ReadAllText(SourcePath(relativePath));
        // The declaration, not the first call: unlike the handlers FolderMoveCopyCallSiteTests
        // reads, this method's name appears at ten call sites before it is defined, and matching
        // one of those would silently measure a single line and pass or fail for the wrong reason.
        var start = Regex.Match(
            source,
            $@"^(?<indent>[ \t]*)(private|internal|public|protected)[^\r\n]*\b{Regex.Escape(methodName)}\s*\(",
            RegexOptions.Multiline);
        Assert.True(start.Success, $"{methodName} not declared in {relativePath} — renamed or removed?");

        var end = Regex.Match(
            source[start.Index..], $@"^{start.Groups["indent"].Value}}}", RegexOptions.Multiline);
        Assert.True(end.Success, $"could not find the end of {methodName} in {relativePath}.");

        return source.Substring(start.Index, end.Index + end.Length);
    }

    private static string SourcePath(string relativePath)
    {
        var path = Path.Combine(RepoRoot(), "QuickMail", relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"{path} not found.");
        return path;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "QuickMail", "Views")))
                return dir.FullName;
        }
        throw new InvalidOperationException($"Repo source tree not found from {AppContext.BaseDirectory}.");
    }
}
