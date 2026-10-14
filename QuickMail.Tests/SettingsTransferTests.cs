using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Settings export and import (issue #507, spec Revision 2): the bundle format, the account-id
/// rewrite, the startup apply with its rollback copy, and the folder heal. Everything runs against
/// real temp directories — the import is file work and nothing else.
/// </summary>
public sealed class SettingsTransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qm-settings-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    // Two accounts on the "desktop" and the same two, under different ids, on the "laptop".
    private static readonly Guid DesktopImap = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DesktopGraph = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid DesktopShared = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid LaptopImap = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid LaptopGraph = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string NewDir(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private static void WriteJson<T>(string dir, string name, T value) =>
        File.WriteAllText(Path.Combine(dir, name), JsonSerializer.Serialize(value, Indented));

    private static List<T> ReadJson<T>(string dir, string name) =>
        JsonSerializer.Deserialize<List<T>>(File.ReadAllText(Path.Combine(dir, name))) ?? [];

    private static AccountModel Imap(Guid id, string user = "kelly@example.com") => new()
    {
        Id = id, Username = user, AccountName = "Home", ImapHost = "imap.example.com",
        BackendKind = BackendKind.ImapSmtp, Signature = "Kelly",
    };

    private static AccountModel Graph(Guid id, string user = "work@example.com") => new()
    {
        Id = id, Username = user, AccountName = "Work", BackendKind = BackendKind.MicrosoftGraph,
        AuthType = AuthType.OAuth2Microsoft, ArchiveFolderFullName = "graph-archive-id",
    };

    /// <summary>A desktop profile with something in every category.</summary>
    private string SeedDesktop()
    {
        var dir = NewDir("desktop");
        var shared = new AccountModel
        {
            Id = DesktopShared, Username = "team@example.com", IsShared = true, ParentAccountId = DesktopGraph,
            BackendKind = BackendKind.MicrosoftGraph, AuthType = AuthType.OAuth2Microsoft,
        };
        WriteJson(dir, "accounts.json", new List<AccountModel> { Imap(DesktopImap), Graph(DesktopGraph), shared });
        WriteJson(dir, "rules.json", new List<MailRule>
        {
            new() { Name = "Receipts", AccountId = DesktopImap, Action = RuleAction.MoveToFolder, TargetFolder = "INBOX/Receipts" },
            new() { Name = "Work news", AccountId = DesktopGraph, Action = RuleAction.MoveToFolder, TargetFolder = "graph-news-id" },
        });
        WriteJson(dir, "views.json", new List<SavedView>
        {
            new() { Name = "Both inboxes", Folders =
            [
                new ViewFolder { AccountId = DesktopImap, FolderFullName = "INBOX" },
                new ViewFolder { AccountId = DesktopGraph, FolderFullName = "graph-inbox-id" },
            ] },
        });
        WriteJson(dir, "templates.json", new List<MessageTemplate> { new() { Title = "Thanks" } });
        // With a byte-order mark, as ConfigService writes it (found in VM testing: the preview called this damaged).
        File.WriteAllText(Path.Combine(dir, "hotkeys.json"), """[ { "CommandId": "mail.reply", "Gesture": "Ctrl+R" } ]""", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(dir, "custom.lex"), Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("QuickMail\r\n")).ToArray());
        Directory.CreateDirectory(Path.Combine(dir, "themes"));
        File.WriteAllText(Path.Combine(dir, "themes", "mine.json"), """{ "id": "mine" }""");
        WriteJson(dir, "contacts.json", new List<ContactModel>
        {
            new() { Id = 1, DisplayName = "Alice", EmailAddress = "alice@example.com" },
            new() { Id = 2, DisplayName = "Bob", EmailAddress = "bob@example.com", Source = ContactSource.Microsoft, OwnerAccountId = DesktopGraph, SourceId = "g-bob" },
        });
        WriteJson(dir, "groups.json", new List<GroupModel> { new() { Id = 1, Name = "Friends", MemberContactIds = [1, 2] } });

        var cfg = new ConfigModel
        {
            PreviewLines = 5,
            AppearanceThemeId = "dark",
            LastRunVersion = "0.8.55",
            GoogleClientSecret = "desktop-secret",
            SaveMessageFolder = @"C:\Users\desktop\Mail",
            StartupFolder = "INBOX",
            StartupFolderAccount = DesktopImap.ToString(),
            DefaultCalendarSource = $"{DesktopGraph}|cal-1",
        };
        cfg.Accounts[DesktopGraph] = new AccountOverrideConfig { PreviewLines = 1, LastMoveFolder = "graph-news-id" };
        File.WriteAllText(Path.Combine(dir, "config.ini"), ConfigService.BuildFileText(cfg));
        return dir;
    }

    private string Export(string profileDir, IReadOnlyCollection<SettingsCategory>? categories = null,
                          IReadOnlyList<CalendarEvent>? calendar = null,
                          IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>>? folders = null)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + SettingsBundle.FileExtension);
        using var stream = File.Create(path);
        SettingsBundle.Write(new SettingsBundle.ExportInputs
        {
            ProfileDir = profileDir,
            Categories = categories ?? SettingsCategoryInfo.All.Select(c => c.Category).ToList(),
            AppVersion = "0.8.55",
            DeviceName = "DESKTOP",
            NowUtc = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc),
            LocalCalendarEvents = calendar ?? [],
            FolderPaths = folders ?? new Dictionary<Guid, IReadOnlyDictionary<string, string>>
            {
                [DesktopGraph] = new Dictionary<string, string> { ["graph-news-id"] = "Inbox/News", ["graph-inbox-id"] = "Inbox", ["graph-archive-id"] = "Archive" },
            },
        }, stream);
        return path;
    }

    private static readonly DateTime Now = new(2026, 10, 12, 9, 30, 0);

    private static SettingsImportResult Import(string bundlePath, string profileDir) =>
        SettingsImporter.Apply(SettingsBundle.Open(bundlePath), profileDir, Now);

    private static string AllText(string dir) =>
        string.Join("\n", Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly)
            .Where(f => !f.EndsWith(".lex", StringComparison.Ordinal))
            .Select(File.ReadAllText));

    // ── Bundle format ──────────────────────────────────────────────────────────

    [Fact]
    public void Export_WritesManifestAndTheChosenCategoriesOnly()
    {
        var desktop = SeedDesktop();
        var bundle = SettingsBundle.Open(Export(desktop, [SettingsCategory.Templates, SettingsCategory.Keyboard]));

        Assert.Equal([SettingsCategory.Templates, SettingsCategory.Keyboard], bundle.Manifest.Categories);
        Assert.NotNull(bundle.Get("templates.json"));
        Assert.NotNull(bundle.Get("hotkeys.json"));
        Assert.Null(bundle.Get("accounts.json"));
        Assert.Null(bundle.Get("rules.json"));
        // The account identities travel even without the accounts: the import maps references with them.
        Assert.Equal(3, bundle.Manifest.Accounts.Count);
        Assert.Contains(bundle.Manifest.Accounts, a => a.Id == DesktopGraph && a.Username == "work@example.com");
    }

    [Fact]
    public void Export_AddressBook_CarriesLocalContactsOnlyAndTheSyncedGroupMembersByAddress()
    {
        var bundle = SettingsBundle.Open(Export(SeedDesktop(), [SettingsCategory.AddressBook]));

        var contacts = JsonSerializer.Deserialize<List<ContactModel>>(bundle.GetText("contacts.json")!)!;
        Assert.Single(contacts);
        Assert.Equal("Alice", contacts[0].DisplayName);
        var members = JsonSerializer.Deserialize<Dictionary<string, string>>(bundle.GetText(SettingsBundle.GroupMembersName)!)!;
        Assert.Equal("bob@example.com", members["2"]);
    }

    [Fact]
    public void Export_NeverCarriesACredentialOrTheMailCache()
    {
        var desktop = SeedDesktop();
        File.WriteAllText(Path.Combine(desktop, "msal.cache"), "token");
        File.WriteAllText(Path.Combine(desktop, "mail.db"), "cache");
        var bundle = SettingsBundle.Open(Export(desktop));

        Assert.DoesNotContain(bundle.Entries.Keys, k => k.Contains("msal", StringComparison.OrdinalIgnoreCase) || k.Contains("mail.db", StringComparison.OrdinalIgnoreCase));
        // The Google client secret is in config.ini on the way out, but the import keeps this computer's (R7).
        Assert.NotNull(bundle.Get("config.ini"));
    }

    [Fact]
    public void Open_RejectsAFileThatIsNotASettingsFile()
    {
        var path = Path.Combine(NewDir("x"), "bad.qmsettings");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("not a zip at all"));
        var ex = Assert.Throws<SettingsBundleException>(() => SettingsBundle.Open(path));
        Assert.Equal("This file could not be read as a QuickMail settings file.", ex.Message);
    }

    [Fact]
    public void Open_RejectsATruncatedFile()
    {
        var full = File.ReadAllBytes(Export(SeedDesktop()));
        var path = Path.Combine(_root, "truncated.qmsettings");
        File.WriteAllBytes(path, full.Take(100).ToArray());
        Assert.Throws<SettingsBundleException>(() => SettingsBundle.Open(path));
    }

    [Fact]
    public void Open_RejectsAZipWithNoManifest()
    {
        var path = Path.Combine(NewDir("y"), "nomanifest.qmsettings");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            zip.CreateEntry("rules.json");
        Assert.Throws<SettingsBundleException>(() => SettingsBundle.Open(path));
    }

    [Fact]
    public void Open_RejectsANewerSchemaWithTheVersionThatMadeIt()
    {
        var path = Path.Combine(NewDir("z"), "newer.qmsettings");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(zip.CreateEntry(SettingsBundle.ManifestName).Open());
            w.Write("""{ "SchemaVersion": 999, "AppVersion": "0.9.2" }""");
        }
        var ex = Assert.Throws<SettingsBundleException>(() => SettingsBundle.Open(path));
        Assert.Equal("This file was made by a newer version of QuickMail (0.9.2). Update QuickMail and try again.", ex.Message);
    }

    [Theory]
    [InlineData("../evil.json")]
    [InlineData("..\\evil.json")]
    [InlineData("themes/../../evil.json")]
    [InlineData("themes/sub/evil.json")]
    [InlineData("themes/evil.exe")]
    [InlineData("C:/Windows/evil.json")]
    [InlineData("unknown.json")]
    public void Open_IgnoresEntriesOutsideTheKnownNames(string name)
    {
        Assert.False(SettingsBundle.IsAcceptedName(name.Replace('\\', '/')));
    }

    [Fact]
    public void Open_AcceptsKnownFilesAndPlainThemeFiles()
    {
        Assert.True(SettingsBundle.IsAcceptedName("rules.json"));
        Assert.True(SettingsBundle.IsAcceptedName("themes/my-theme.json"));
    }

    [Fact]
    public void BuildFolderPaths_WalksGraphParentsAndKeepsImapPaths()
    {
        var acct = Guid.NewGuid();
        var imap = Guid.NewGuid();
        var paths = SettingsBundle.BuildFolderPaths(new Dictionary<Guid, List<MailFolderModel>>
        {
            [acct] =
            [
                new() { FullName = "inbox-id", DisplayName = "Inbox", ParentId = "root-not-listed" },
                new() { FullName = "news-id", DisplayName = "News", ParentId = "inbox-id" },
                new() { FullName = "loop-a", DisplayName = "A", ParentId = "loop-b" },
                new() { FullName = "loop-b", DisplayName = "B", ParentId = "loop-a" },
                new() { FullName = "", DisplayName = "Header", IsHeader = true },
            ],
            [imap] = [new() { FullName = "INBOX/Receipts", DisplayName = "Receipts" }],
        });

        Assert.Equal("Inbox", paths[acct]["inbox-id"]);
        Assert.Equal("Inbox/News", paths[acct]["news-id"]);
        Assert.Equal("B/A", paths[acct]["loop-a"]);   // a cycle stops rather than spinning
        Assert.Equal("INBOX/Receipts", paths[imap]["INBOX/Receipts"]);
    }

    // ── Account id rewrite ─────────────────────────────────────────────────────

    [Fact]
    public void Rewrite_ReplacesBothFormsAndKeepsCase()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var text = $"{a} {a.ToString("N")} [account:{a.ToString().ToUpperInvariant()}] {a}|cal";
        var result = AccountIdRewriter.Rewrite(text, new Dictionary<Guid, Guid> { [a] = b });
        Assert.Equal($"{b} {b.ToString("N")} [account:{b.ToString().ToUpperInvariant()}] {b}|cal", result);
    }

    [Fact]
    public void Rewrite_SwapsInOnePass()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var result = AccountIdRewriter.Rewrite($"{a},{b}", new Dictionary<Guid, Guid> { [a] = b, [b] = a });
        Assert.Equal($"{b},{a}", result);
    }

    [Fact]
    public void Rewrite_LeavesTextAloneWhenNothingMoves()
    {
        var a = Guid.NewGuid();
        Assert.Equal($"x {a}", AccountIdRewriter.Rewrite($"x {a}", new Dictionary<Guid, Guid> { [a] = a }));
    }

    // ── Import into an empty profile (the new-computer case) ───────────────────

    [Fact]
    public void Import_IntoEmptyProfile_KeepsEveryAccountIdSoEveryReferenceResolves()
    {
        var bundle = Export(SeedDesktop());
        var laptop = NewDir("laptop");

        var result = Import(bundle, laptop);

        Assert.True(result.Succeeded);
        var accounts = ReadJson<AccountModel>(laptop, "accounts.json");
        Assert.Equal([DesktopImap, DesktopGraph, DesktopShared], accounts.Select(a => a.Id));
        Assert.Equal(DesktopGraph, accounts.Single(a => a.IsShared).ParentAccountId);
        Assert.Equal("Kelly", accounts.Single(a => a.Id == DesktopImap).Signature);
        Assert.All(ReadJson<MailRule>(laptop, "rules.json"), r => Assert.True(r.IsEnabled));
        Assert.Equal(2, ReadJson<SavedView>(laptop, "views.json").Single().Folders.Count);
        // The shared mailbox signs in through its parent, so it is not on the sign-in list.
        Assert.Equal([DesktopImap, DesktopGraph], result.AccountsNeedingSignIn);
        Assert.True(Directory.Exists(Path.Combine(laptop, result.BackupFolder)));
    }

    [Fact]
    public void Import_IntoEmptyProfile_BringsEveryCategory()
    {
        var desktop = SeedDesktop();
        var laptop = NewDir("laptop");
        Import(Export(desktop), laptop);

        Assert.Single(ReadJson<MessageTemplate>(laptop, "templates.json"));
        Assert.Equal(File.ReadAllText(Path.Combine(desktop, "hotkeys.json")), File.ReadAllText(Path.Combine(laptop, "hotkeys.json")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(desktop, "custom.lex")), File.ReadAllBytes(Path.Combine(laptop, "custom.lex")));
        Assert.True(File.Exists(Path.Combine(laptop, "themes", "mine.json")));
        var cfg = ConfigService.ParseFile(File.ReadAllLines(Path.Combine(laptop, "config.ini")));
        Assert.Equal(5, cfg.PreviewLines);
        Assert.Equal("dark", cfg.AppearanceThemeId);
        Assert.Equal(DesktopImap.ToString(), cfg.StartupFolderAccount);
    }

    // ── Import into a profile that already has the accounts (the #507 case) ─────

    private string SeedLaptop()
    {
        var dir = NewDir("laptop");
        var other = Imap(Guid.NewGuid(), "other@example.com");
        WriteJson(dir, "accounts.json", new List<AccountModel> { Imap(LaptopImap), Graph(LaptopGraph), other });
        WriteJson(dir, "rules.json", new List<MailRule> { new() { Name = "Laptop only", AccountId = LaptopImap } });
        WriteJson(dir, "templates.json", new List<MessageTemplate> { new() { Title = "Old" }, new() { Title = "Older" } });
        WriteJson(dir, "contacts.json", new List<ContactModel>
        {
            new() { Id = 7, DisplayName = "Laptop local", EmailAddress = "local@example.com" },
            new() { Id = 40, DisplayName = "Bob", EmailAddress = "BOB@example.com", Source = ContactSource.Microsoft, OwnerAccountId = LaptopGraph, SourceId = "g-bob" },
        });
        var cfg = new ConfigModel
        {
            LastRunVersion = "0.8.40",
            GoogleClientSecret = "laptop-secret",
            SaveMessageFolder = @"D:\Mail",
            PreviewLines = 2,
        };
        cfg.Accounts[LaptopImap] = new AccountOverrideConfig { PreviewLines = 4 };
        File.WriteAllText(Path.Combine(dir, "config.ini"), ConfigService.BuildFileText(cfg));
        return dir;
    }

    [Fact]
    public void Import_IntoPopulatedProfile_MatchesAccountsAndPointsEverythingAtTheLocalIds()
    {
        var bundle = Export(SeedDesktop());
        var laptop = SeedLaptop();

        var result = Import(bundle, laptop);

        var accounts = ReadJson<AccountModel>(laptop, "accounts.json");
        // Matched by username and backend: no duplicates, local ids kept (so saved passwords still apply),
        // the account only the laptop had is kept, and the shared mailbox is added under the laptop's parent.
        Assert.Equal(4, accounts.Count);
        Assert.Contains(accounts, a => a.Id == LaptopImap && a.Signature == "Kelly");
        Assert.Contains(accounts, a => a.Username == "other@example.com");
        Assert.Equal(LaptopGraph, accounts.Single(a => a.IsShared).ParentAccountId);
        Assert.DoesNotContain(result.AccountsNeedingSignIn, id => id == LaptopImap || id == LaptopGraph);

        var rules = ReadJson<MailRule>(laptop, "rules.json");
        Assert.Equal(LaptopImap, rules.Single(r => r.Name == "Receipts").AccountId);
        Assert.Equal(LaptopGraph, rules.Single(r => r.Name == "Work news").AccountId);
        Assert.Equal([LaptopImap, LaptopGraph], ReadJson<SavedView>(laptop, "views.json").Single().Folders.Select(f => f.AccountId));
    }

    [Fact]
    public void Import_IntoPopulatedProfile_LeavesNoSourceAccountIdInAnyFile()
    {
        var bundle = Export(SeedDesktop());
        var laptop = SeedLaptop();
        Import(bundle, laptop);

        // The shared mailbox keeps its own id (nothing here has it); the two matched accounts must not survive anywhere.
        var text = AllText(laptop);
        Assert.False(AccountIdRewriter.ContainsAny(text, [DesktopImap, DesktopGraph]),
            "a source account id survived the import: " + text);
    }

    [Fact]
    public void Import_Replaces_RulesTemplatesAndViews()
    {
        var bundle = Export(SeedDesktop());
        var laptop = SeedLaptop();
        Import(bundle, laptop);

        Assert.DoesNotContain(ReadJson<MailRule>(laptop, "rules.json"), r => r.Name == "Laptop only");
        Assert.Equal(["Thanks"], ReadJson<MessageTemplate>(laptop, "templates.json").Select(t => t.Title));
    }

    [Fact]
    public void Import_TwiceGivesTheSameResult()
    {
        var bundle = Export(SeedDesktop());
        var laptop = SeedLaptop();
        Import(bundle, laptop);
        var first = new[] { "accounts.json", "rules.json", "views.json", "contacts.json", "groups.json", "config.ini", "templates.json" }
            .ToDictionary(f => f, f => File.ReadAllText(Path.Combine(laptop, f)));

        Import(bundle, laptop);

        foreach (var (file, text) in first)
            Assert.Equal(text, File.ReadAllText(Path.Combine(laptop, file)));
    }

    [Fact]
    public void Import_Preferences_KeepsThisComputersOwnSettingsAndRewritesAccountSections()
    {
        var bundle = Export(SeedDesktop());
        var laptop = SeedLaptop();
        Import(bundle, laptop);

        var cfg = ConfigService.ParseFile(File.ReadAllLines(Path.Combine(laptop, "config.ini")));
        Assert.Equal(5, cfg.PreviewLines);                       // travels
        Assert.Equal("0.8.40", cfg.LastRunVersion);              // this install
        Assert.Equal("laptop-secret", cfg.GoogleClientSecret);   // a credential: never moved
        Assert.Equal(@"D:\Mail", cfg.SaveMessageFolder);         // a path on this disk
        Assert.Equal($"{LaptopGraph}|cal-1", cfg.DefaultCalendarSource);
        Assert.Equal(LaptopImap.ToString(), cfg.StartupFolderAccount);
        Assert.Equal(1, cfg.Accounts[LaptopGraph].PreviewLines);   // from the file, under the local id
        Assert.False(cfg.Accounts.ContainsKey(LaptopImap));        // the file's override set for this account: none
    }

    [Fact]
    public void Import_AddressBook_ReplacesLocalContactsKeepsSyncedOnesAndRewiresGroups()
    {
        var bundle = Export(SeedDesktop());
        var laptop = SeedLaptop();
        Import(bundle, laptop);

        var contacts = ReadJson<ContactModel>(laptop, "contacts.json");
        Assert.DoesNotContain(contacts, c => c.DisplayName == "Laptop local");
        var alice = contacts.Single(c => c.DisplayName == "Alice");
        var bob = contacts.Single(c => c.DisplayName == "Bob");
        Assert.True(alice.Id > 40);            // renumbered above every id kept here
        Assert.Equal(40, bob.Id);              // the synced contact is untouched
        var group = ReadJson<GroupModel>(laptop, "groups.json").Single();
        Assert.Equal([alice.Id, 40], group.MemberContactIds);   // Bob found here by address
        Assert.Equal(contacts.Count, contacts.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void Import_AddressBook_DropsAndCountsASyncedMemberThatIsNotHere()
    {
        var bundle = Export(SeedDesktop());
        var laptop = NewDir("fresh");
        var result = Import(bundle, laptop);

        Assert.Equal([ReadJson<ContactModel>(laptop, "contacts.json").Single().Id],
                     ReadJson<GroupModel>(laptop, "groups.json").Single().MemberContactIds);
        Assert.Contains(result.Lines, l => l.Contains("1 group member synced from a server could not be found", StringComparison.Ordinal));
    }

    [Fact]
    public void Import_AmbiguousMatchIsSkippedNotGuessed()
    {
        var bundle = Export(SeedDesktop(), [SettingsCategory.Accounts, SettingsCategory.RulesAndViews]);
        var laptop = NewDir("twin");
        // Two local accounts claim the same identity: which one the rules meant is unknowable.
        var twinA = Imap(Guid.NewGuid());
        var twinB = Imap(Guid.NewGuid());
        WriteJson(laptop, "accounts.json", new List<AccountModel> { twinA, twinB });

        var plan = SettingsImporter.Plan(SettingsBundle.Open(bundle), laptop);
        Assert.Equal(ImportAccountOutcome.Skipped, plan.Accounts.Single(a => a.Source.Id == DesktopImap).Outcome);

        var result = Import(bundle, laptop);
        Assert.Contains(result.Lines, l => l.Contains("matches more than one account", StringComparison.Ordinal));
        Assert.False(ReadJson<MailRule>(laptop, "rules.json").Single(r => r.Name == "Receipts").IsEnabled);
    }

    [Fact]
    public void Import_WithoutAccounts_TurnsOffWhatPointsAtAMissingAccount()
    {
        var bundle = Export(SeedDesktop(), [SettingsCategory.RulesAndViews, SettingsCategory.Preferences]);
        var laptop = NewDir("bare");
        WriteJson(laptop, "accounts.json", new List<AccountModel> { Imap(LaptopImap) });

        var result = Import(bundle, laptop);

        var rules = ReadJson<MailRule>(laptop, "rules.json");
        Assert.True(rules.Single(r => r.Name == "Receipts").IsEnabled);      // matched: still works
        Assert.False(rules.Single(r => r.Name == "Work news").IsEnabled);    // its account is not here
        Assert.Equal([LaptopImap], ReadJson<SavedView>(laptop, "views.json").Single().Folders.Select(f => f.AccountId));
        var cfg = ConfigService.ParseFile(File.ReadAllLines(Path.Combine(laptop, "config.ini")));
        Assert.Equal(string.Empty, cfg.DefaultCalendarSource);
        Assert.False(cfg.Accounts.ContainsKey(DesktopGraph));
        Assert.Contains(result.Lines, l => l.Contains("Work news", StringComparison.Ordinal));
        Assert.Single(ReadJson<AccountModel>(laptop, "accounts.json"));     // nothing added
    }

    [Fact]
    public void Import_SharedMailboxWithoutItsAccountIsSkipped()
    {
        var desktop = SeedDesktop();
        // The shared mailbox's parent is not in the file's accounts.
        var accounts = ReadJson<AccountModel>(desktop, "accounts.json").Where(a => a.Id != DesktopGraph).ToList();
        WriteJson(desktop, "accounts.json", accounts);
        var laptop = NewDir("shared");

        Import(Export(desktop, [SettingsCategory.Accounts]), laptop);

        Assert.DoesNotContain(ReadJson<AccountModel>(laptop, "accounts.json"), a => a.IsShared);
    }

    [Fact]
    public void Import_AnAccountIdAlreadyTakenHereGetsANewOne()
    {
        var bundle = Export(SeedDesktop(), [SettingsCategory.Accounts, SettingsCategory.RulesAndViews]);
        var laptop = NewDir("collide");
        // A different account here happens to hold the desktop's id.
        WriteJson(laptop, "accounts.json", new List<AccountModel> { Imap(DesktopImap, "someone-else@example.com") });

        Import(bundle, laptop);

        var accounts = ReadJson<AccountModel>(laptop, "accounts.json");
        Assert.Equal(accounts.Count, accounts.Select(a => a.Id).Distinct().Count());
        var kelly = accounts.Single(a => a.Username == "kelly@example.com");
        Assert.NotEqual(DesktopImap, kelly.Id);
        Assert.Equal(kelly.Id, ReadJson<MailRule>(laptop, "rules.json").Single(r => r.Name == "Receipts").AccountId);
    }

    [Fact]
    public void Import_KeyboardWithNoCustomShortcutsRestoresTheDefaults()
    {
        var desktop = SeedDesktop();
        File.Delete(Path.Combine(desktop, "hotkeys.json"));
        var laptop = NewDir("keys");
        File.WriteAllText(Path.Combine(laptop, "hotkeys.json"), "[]");

        Import(Export(desktop, [SettingsCategory.Keyboard]), laptop);

        Assert.False(File.Exists(Path.Combine(laptop, "hotkeys.json")));
    }

    [Fact]
    public void Import_ThemesAreMirrored()
    {
        var bundle = Export(SeedDesktop(), [SettingsCategory.Appearance]);
        var laptop = NewDir("themes");
        Directory.CreateDirectory(Path.Combine(laptop, "themes"));
        File.WriteAllText(Path.Combine(laptop, "themes", "laptop-only.json"), "{}");

        Import(bundle, laptop);

        Assert.Equal(["mine.json"], Directory.GetFiles(Path.Combine(laptop, "themes")).Select(Path.GetFileName));
    }

    [Fact]
    public void Import_CategoriesNotInTheFileAreLeftAlone()
    {
        var bundle = Export(SeedDesktop(), [SettingsCategory.Templates]);
        var laptop = SeedLaptop();
        var rulesBefore = File.ReadAllText(Path.Combine(laptop, "rules.json"));
        var accountsBefore = File.ReadAllText(Path.Combine(laptop, "accounts.json"));

        Import(bundle, laptop);

        Assert.Equal(rulesBefore, File.ReadAllText(Path.Combine(laptop, "rules.json")));
        Assert.Equal(accountsBefore, File.ReadAllText(Path.Combine(laptop, "accounts.json")));
    }

    // ── Rollback ───────────────────────────────────────────────────────────────

    [Fact]
    public void Import_FailingPartWay_RestoresEveryFileByteForByte()
    {
        var bundle = Export(SeedDesktop());
        var laptop = SeedLaptop();
        File.WriteAllText(Path.Combine(laptop, "custom.lex"), "laptop words");
        var before = Directory.GetFiles(laptop).ToDictionary(Path.GetFileName, File.ReadAllBytes);

        // custom.lex is written last; holding it open for reading lets the backup copy it but makes the
        // import fail after everything else has changed.
        SettingsImportFailedException ex;
        using (new FileStream(Path.Combine(laptop, "custom.lex"), FileMode.Open, FileAccess.Read, FileShare.Read))
            ex = Assert.Throws<SettingsImportFailedException>(() => Import(bundle, laptop));

        Assert.StartsWith("The import failed and your previous settings were restored from backup-", ex.Message, StringComparison.Ordinal);
        foreach (var (name, bytes) in before)
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(laptop, name!)));
        Assert.Equal(before.Keys.OrderBy(k => k), Directory.GetFiles(laptop).Select(Path.GetFileName).OrderBy(k => k));
        Assert.False(Directory.Exists(Path.Combine(laptop, "themes")) && Directory.GetFiles(Path.Combine(laptop, "themes")).Length > 0);
    }

    [Fact]
    public void Import_WhenTheBackupCannotBeMade_ChangesNothingAndLeavesNoHalfBackup()
    {
        var bundle = Export(SeedDesktop());
        var laptop = SeedLaptop();
        var before = Directory.GetFiles(laptop).ToDictionary(Path.GetFileName, File.ReadAllBytes);

        SettingsBundleException ex;
        using (new FileStream(Path.Combine(laptop, "rules.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            ex = Assert.Throws<SettingsBundleException>(() => Import(bundle, laptop));

        Assert.EndsWith("so nothing was changed.", ex.Message.Split(". ")[0] + ".", StringComparison.Ordinal);
        Assert.Empty(Directory.GetDirectories(laptop, "backup-*"));
        foreach (var (name, bytes) in before)
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(laptop, name!)));
    }

    [Fact]
    public void ApplyPendingAtStartup_AppliesRecordsAndRemovesTheStagedFile()
    {
        var bundle = Export(SeedDesktop());
        var laptop = NewDir("staged");
        SettingsImporter.Stage(bundle, laptop);

        var result = SettingsImporter.ApplyPendingAtStartup(laptop, Now);

        Assert.NotNull(result);
        Assert.True(result.Succeeded);
        Assert.False(File.Exists(Path.Combine(laptop, SettingsImporter.PendingFileName)));
        var recorded = SettingsImporter.TakeResult(laptop);
        Assert.NotNull(recorded);
        Assert.Equal(result.AccountsNeedingSignIn, recorded.AccountsNeedingSignIn);
        Assert.StartsWith("Settings imported from DESKTOP.", recorded.Report, StringComparison.Ordinal);
        Assert.Null(SettingsImporter.TakeResult(laptop));   // shown once
    }

    [Fact]
    public void ApplyPendingAtStartup_ACorruptFileWritesNothingAndIsNotRetried()
    {
        var laptop = SeedLaptop();
        File.WriteAllText(Path.Combine(laptop, SettingsImporter.PendingFileName), "garbage");
        var before = Directory.GetFiles(laptop).Where(f => !f.EndsWith(SettingsBundle.FileExtension, StringComparison.Ordinal))
            .ToDictionary(Path.GetFileName, File.ReadAllText);

        var result = SettingsImporter.ApplyPendingAtStartup(laptop, Now);

        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.False(File.Exists(Path.Combine(laptop, SettingsImporter.PendingFileName)));
        Assert.Empty(Directory.GetDirectories(laptop, "backup-*"));
        foreach (var (name, text) in before)
            Assert.Equal(text, File.ReadAllText(Path.Combine(laptop, name!)));
    }

    [Fact]
    public void ApplyPendingAtStartup_NothingStagedDoesNothing()
    {
        Assert.Null(SettingsImporter.ApplyPendingAtStartup(NewDir("none"), Now));
    }

    // ── The Local Calendar ─────────────────────────────────────────────────────

    private sealed class CalendarStore : StubLocalStoreService
    {
        public List<CalendarEvent> Events { get; } = [];
        public override Task<List<CalendarEvent>> LoadCalendarEventsAsync() => Task.FromResult(Events.ToList());
        public override Task UpsertCalendarEventAsync(CalendarEvent evt)
        {
            Events.RemoveAll(e => e.Uid == evt.Uid && e.AccountId == evt.AccountId);
            Events.Add(evt);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task LocalCalendar_TravelsAndARepeatImportUpdatesRatherThanDuplicates()
    {
        var series = new CalendarEvent
        {
            Uid = "standup@local", AccountId = CalendarEvent.LocalAccountId, Summary = "Standup",
            StartTimeTicks = new DateTime(2026, 10, 5, 14, 0, 0, DateTimeKind.Utc).Ticks,
            EndTimeTicks = new DateTime(2026, 10, 5, 14, 15, 0, DateTimeKind.Utc).Ticks,
            RecurrenceRule = "FREQ=DAILY;COUNT=10",
        };
        var skipped = new DateTime(2026, 10, 7, 14, 0, 0, DateTimeKind.Utc).ToLocalTime();
        series.AddExDate(skipped);
        var bundle = Export(SeedDesktop(), [SettingsCategory.LocalCalendar], calendar: [series]);
        var laptop = NewDir("cal");
        var store = new CalendarStore();
        store.Events.Add(new CalendarEvent { Uid = "dentist@local", AccountId = CalendarEvent.LocalAccountId, Summary = "Dentist" });

        Import(bundle, laptop);
        await SettingsImporter.ApplyPendingCalendarAsync(laptop, store);
        Import(bundle, laptop);
        await SettingsImporter.ApplyPendingCalendarAsync(laptop, store);

        Assert.Equal(2, store.Events.Count);                                   // nothing deleted, nothing doubled
        var imported = store.Events.Single(e => e.Uid == "standup@local");
        Assert.Equal("FREQ=DAILY;COUNT=10", imported.RecurrenceRule);
        Assert.Equal([skipped], imported.GetExDates());                        // the deleted occurrence stays deleted
        Assert.False(File.Exists(Path.Combine(laptop, SettingsImporter.PendingCalendarFileName)));
    }

    private sealed class BrokenStore : StubLocalStoreService
    {
        public int Loads { get; private set; }
        public override Task<List<CalendarEvent>> LoadCalendarEventsAsync()
        {
            Loads++;
            throw new InvalidOperationException("not an IO failure");
        }
    }

    [Fact]
    public async Task LocalCalendar_AnyFailureSetsTheFileAsideOnceAndNeverStopsStartup()
    {
        var laptop = NewDir("calfail");
        File.WriteAllText(Path.Combine(laptop, SettingsImporter.PendingCalendarFileName), "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n");
        var store = new BrokenStore();

        await SettingsImporter.ApplyPendingCalendarAsync(laptop, store);   // must not throw
        await SettingsImporter.ApplyPendingCalendarAsync(laptop, store);   // and is not tried again

        Assert.Equal(1, store.Loads);
        Assert.False(File.Exists(Path.Combine(laptop, SettingsImporter.PendingCalendarFileName)));
        Assert.True(File.Exists(Path.Combine(laptop, SettingsImporter.FailedCalendarFileName)));
        var result = SettingsImporter.TakeResult(laptop);
        Assert.NotNull(result);
        // No import result was waiting, so this is the calendar's own, not a second "Settings imported".
        Assert.StartsWith("The Local Calendar from your imported settings.", result.Report, StringComparison.Ordinal);
        Assert.Contains(SettingsImporter.FailedCalendarFileName, result.Report, StringComparison.Ordinal);
    }

    [Fact]
    public void AllDaySeries_ExportsDeletedDaysAsDatesAndRoundTrips()
    {
        var day = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Local);
        var series = new CalendarEvent
        {
            Uid = "bins@local", AccountId = CalendarEvent.LocalAccountId, Summary = "Bins", IsAllDay = true,
            StartTimeTicks = day.ToUniversalTime().Ticks,
            EndTimeTicks = day.AddDays(1).AddSeconds(-1).ToUniversalTime().Ticks,
            RecurrenceRule = "FREQ=WEEKLY;COUNT=4",
        };
        series.AddExDate(day.AddDays(7));

        var ics = IcsModel.ExportCalendar([series]);

        Assert.Contains("EXDATE;VALUE=DATE:20261012", ics, StringComparison.Ordinal);
        var back = CalendarIcsImporter.Plan(ics, []).Events.Single();
        Assert.Equal([day.AddDays(7)], back.GetExDates());
    }

    [Fact]
    public void Restore_NamesWhatItCouldNotPutBack()
    {
        var dir = SeedLaptop();
        var backup = SettingsImporter.CreateBackup(dir, Now);
        File.WriteAllText(Path.Combine(dir, "rules.json"), "[]");
        File.WriteAllText(Path.Combine(dir, "templates.json"), "[]");

        List<string> failed;
        using (new FileStream(Path.Combine(dir, "rules.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            failed = SettingsImporter.Restore(dir, backup);

        Assert.Equal(["rules.json"], failed);
        Assert.Equal(File.ReadAllText(Path.Combine(backup, "templates.json")), File.ReadAllText(Path.Combine(dir, "templates.json")));
        var ex = new SettingsImportFailedException("backup-x", failed, new IOException("boom"));
        Assert.StartsWith("The import failed, and these could not be put back: rules.json.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_ASharedMailboxNeverTakesOverAFullAccountWithTheSameAddress()
    {
        var bundle = Export(SeedDesktop(), [SettingsCategory.Accounts]);
        var laptop = NewDir("sharedfull");
        var full = new AccountModel { Id = Guid.NewGuid(), Username = "team@example.com", BackendKind = BackendKind.MicrosoftGraph, AuthType = AuthType.OAuth2Microsoft };
        WriteJson(laptop, "accounts.json", new List<AccountModel> { Graph(LaptopGraph), full });

        Import(bundle, laptop);

        var accounts = ReadJson<AccountModel>(laptop, "accounts.json");
        Assert.False(accounts.Single(a => a.Id == full.Id).IsShared);
        Assert.Single(accounts, a => a.IsShared && a.ParentAccountId == LaptopGraph);
    }

    [Fact]
    public void Import_AMatchedSharedMailboxWhoseAccountIsSkippedIsSkippedToo()
    {
        var bundle = Export(SeedDesktop(), [SettingsCategory.Accounts]);
        var laptop = NewDir("sharedskip");
        var localShared = new AccountModel
        {
            Id = Guid.NewGuid(), Username = "team@example.com", IsShared = true, ParentAccountId = LaptopGraph,
            BackendKind = BackendKind.MicrosoftGraph, AuthType = AuthType.OAuth2Microsoft,
        };
        // Two locals match the file's Microsoft 365 account, so it is skipped — and with it the shared mailbox.
        WriteJson(laptop, "accounts.json", new List<AccountModel> { Graph(LaptopGraph), Graph(Guid.NewGuid()), localShared });

        var plan = SettingsImporter.Plan(SettingsBundle.Open(bundle), laptop);
        Import(bundle, laptop);

        Assert.Equal(ImportAccountOutcome.Skipped, plan.Accounts.Single(r => r.Source.IsShared).Outcome);
        Assert.Equal(LaptopGraph, ReadJson<AccountModel>(laptop, "accounts.json").Single(a => a.IsShared).ParentAccountId);
    }

    [Fact]
    public void Import_AChangedSignInMethodPutsTheAccountOnTheSignInList()
    {
        var bundle = Export(SeedDesktop(), [SettingsCategory.Accounts]);
        var laptop = NewDir("authchange");
        var passwordHere = Graph(LaptopGraph);
        passwordHere.AuthType = AuthType.Password;
        WriteJson(laptop, "accounts.json", new List<AccountModel> { Imap(LaptopImap), passwordHere });

        var result = Import(bundle, laptop);

        Assert.Contains(LaptopGraph, result.AccountsNeedingSignIn);
        Assert.DoesNotContain(LaptopImap, result.AccountsNeedingSignIn);
    }

    [Fact]
    public void Import_AnAddedAccountDoesNotBringTheOtherComputersConversionState()
    {
        var desktop = SeedDesktop();
        var accounts = ReadJson<AccountModel>(desktop, "accounts.json");
        accounts.Single(a => a.Id == DesktopGraph).GraphConversionPending = true;
        WriteJson(desktop, "accounts.json", accounts);
        var laptop = NewDir("conversion");

        Import(Export(desktop, [SettingsCategory.Accounts]), laptop);

        Assert.False(ReadJson<AccountModel>(laptop, "accounts.json").Single(a => a.Id == DesktopGraph).GraphConversionPending);
    }

    [Fact]
    public void Plan_SaysWhenTheSameAddressIsHereAsAnotherKindOfAccount()
    {
        var bundle = Export(SeedDesktop(), [SettingsCategory.Accounts]);
        var laptop = NewDir("otherkind");
        WriteJson(laptop, "accounts.json", new List<AccountModel> { Imap(LaptopImap, "work@example.com") });

        var row = SettingsImporter.Plan(SettingsBundle.Open(bundle), laptop).Accounts.Single(r => r.Source.Id == DesktopGraph);

        Assert.Equal(ImportAccountOutcome.Added, row.Outcome);
        Assert.Equal("Work, will be added; this computer also has this address as a different kind of account", row.ToString());
    }

    [Fact]
    public void Plan_SaysWhatEachCategoryReplacesHere()
    {
        var plan = SettingsImporter.Plan(SettingsBundle.Open(Export(SeedDesktop())), SeedLaptop());
        Assert.Contains("Message templates: 1 template, replacing the 2 here", plan.CategoryLines);
        Assert.Contains(plan.CategoryLines, l => l.StartsWith("Address book: 1 contact and 1 group, replacing the 1 and 0 here", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_RefusesADamagedFile()
    {
        var path = Path.Combine(NewDir("damaged"), "d.qmsettings");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry(SettingsBundle.ManifestName).Open()))
                w.Write("""{ "SchemaVersion": 1, "Categories": [1] }""");
            using (var w = new StreamWriter(zip.CreateEntry("rules.json").Open()))
                w.Write("[ { not json");
        }
        var ex = Assert.Throws<SettingsBundleException>(() => SettingsImporter.Plan(SettingsBundle.Open(path), NewDir("t")));
        Assert.Equal("This settings file is damaged: rules.json could not be read.", ex.Message);
    }

    [Fact]
    public void Import_ASettingTheFileDoesNotMentionKeepsItsValueHere()
    {
        var desktop = SeedDesktop();
        // A file from an older QuickMail, written before this setting existed.
        var lines = File.ReadAllLines(Path.Combine(desktop, "config.ini"))
            .Where(l => !l.TrimStart().StartsWith("CalendarAtEndOfFolderList", StringComparison.OrdinalIgnoreCase));
        File.WriteAllLines(Path.Combine(desktop, "config.ini"), lines);
        var laptop = SeedLaptop();
        var cfg = ConfigService.ParseFile(File.ReadAllLines(Path.Combine(laptop, "config.ini")));
        cfg.CalendarAtEndOfFolderList = true;
        File.WriteAllText(Path.Combine(laptop, "config.ini"), ConfigService.BuildFileText(cfg));

        Import(Export(desktop, [SettingsCategory.Preferences]), laptop);

        var after = ConfigService.ParseFile(File.ReadAllLines(Path.Combine(laptop, "config.ini")));
        Assert.True(after.CalendarAtEndOfFolderList);   // not reset to the default
        Assert.Equal(5, after.PreviewLines);            // what the file does say still arrives
    }

    // ── Folder heal (Graph ids, spec R5) ───────────────────────────────────────

    [Fact]
    public void FolderHeal_TranslatesAnUnknownGraphIdThroughItsPathAndTurnsOffWhatCannotMatch()
    {
        var acct = Guid.NewGuid();
        var rules = new List<MailRule>
        {
            new() { Name = "News", AccountId = acct, Action = RuleAction.MoveToFolder, TargetFolder = "old-news-id" },
            new() { Name = "Gone", AccountId = acct, Action = RuleAction.MoveToFolder, TargetFolder = "old-gone-id" },
            new() { Name = "Same", AccountId = acct, Action = RuleAction.MoveToFolder, TargetFolder = "same-id" },
        };
        var views = new List<SavedView> { new() { Name = "V", Folders = [new ViewFolder { AccountId = acct, FolderFullName = "old-news-id" }] } };
        var cfg = new ConfigModel();
        var account = new AccountModel { Id = acct, ArchiveFolderFullName = "old-archive-id" };
        var folders = new List<MailFolderModel>
        {
            new() { FullName = "new-news-id", DisplayName = "News", ParentId = "new-inbox-id" },
            new() { FullName = "new-inbox-id", DisplayName = "Inbox" },
            new() { FullName = "new-archive-id", DisplayName = "Archive" },
            new() { FullName = "same-id", DisplayName = "Same" },
        };
        var paths = new Dictionary<string, string>
        {
            ["old-news-id"] = "Inbox/News", ["old-gone-id"] = "Inbox/Deleted Since", ["old-archive-id"] = "Archive",
        };

        var report = ImportFolderHeal.Heal(acct, folders, paths, rules, views, cfg, account, out var archiveChanged);

        Assert.Equal("new-news-id", rules[0].TargetFolder);
        Assert.True(rules[0].IsEnabled);
        Assert.False(rules[1].IsEnabled);                        // never retargeted to a guess
        Assert.Equal(["Gone"], report.DisabledRules);
        Assert.Equal("same-id", rules[2].TargetFolder);           // already resolves: untouched
        Assert.Equal("new-news-id", views[0].Folders[0].FolderFullName);
        Assert.True(archiveChanged);
        Assert.Equal("new-archive-id", account.ArchiveFolderFullName);
    }

    [Fact]
    public void FolderHeal_IsTakenOnceAndOnlyForAccountsHere()
    {
        var dir = NewDir("heal");
        var here = Guid.NewGuid();
        var gone = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, string>>
        {
            [here.ToString()] = new() { ["a"] = "A" },
            [gone.ToString()] = new() { ["b"] = "B" },
        });
        ImportFolderHeal.Save(dir, json, new HashSet<Guid> { here });

        Assert.Null(ImportFolderHeal.Take(dir, gone));
        Assert.Equal("A", ImportFolderHeal.Take(dir, here)!["a"]);
        Assert.Null(ImportFolderHeal.Take(dir, here));
        Assert.False(File.Exists(Path.Combine(dir, ImportFolderHeal.FileName)));
    }

    [Fact]
    public void Import_WritesTheFolderNamesForTheHealUnderTheLocalIds()
    {
        var bundle = Export(SeedDesktop());
        var laptop = SeedLaptop();
        Import(bundle, laptop);

        Assert.Equal("Inbox/News", ImportFolderHeal.Take(laptop, LaptopGraph)!["graph-news-id"]);
    }

    // ── Config classification (spec §5.2, R7) ──────────────────────────────────

    [Fact]
    public void EveryConfigSettingIsClassifiedExactlyOnce()
    {
        foreach (var prop in SettingsConfigTransfer.ClassifiedProperties())
        {
            var hits = new[] { SettingsConfigTransfer.Portable, SettingsConfigTransfer.Local, SettingsConfigTransfer.SeparateFile }
                .Count(set => set.Contains(prop.Name));
            Assert.True(hits == 1,
                $"ConfigModel.{prop.Name} must be in exactly one of SettingsConfigTransfer.Portable, Local or SeparateFile (it is in {hits}). "
                + "Decide whether it describes the user's preference (Portable) or this computer (Local).");
        }
    }

    [Fact]
    public void ClassificationNamesOnlyRealSettings()
    {
        var real = SettingsConfigTransfer.ClassifiedProperties().Select(p => p.Name).ToHashSet();
        foreach (var name in SettingsConfigTransfer.Portable.Concat(SettingsConfigTransfer.Local).Concat(SettingsConfigTransfer.SeparateFile))
            Assert.Contains(name, real);
    }

    // ── Every profile file is accounted for (spec §12 ProfileFileCoverageTests) ─

    [Fact]
    public void EveryProfileFileAServiceWritesIsClassified()
    {
        // Files the app writes under the profile directory, found by the same search the review used:
        // Path.Combine(profile.ProfileDir, "<name>") in Services and App. A new one fails here until it
        // is either carried by a category or listed as deliberately left behind.
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mail.db", "msal.cache", "quickmail.log", "connection.log", ".immutable-id-rebuilt", "debug-screenshots",
            // Records that this computer's mail.db had its sender addresses backfilled (#803) — about the cache,
            // which stays behind, so the marker does too.
            ".sender-address-backfilled",
            SettingsImporter.PendingFileName, SettingsImporter.ResultFileName, SettingsImporter.PendingCalendarFileName,
            ImportFolderHeal.FileName,
        };
        var carried = SettingsBundle.CategoryFiles.Values.SelectMany(f => f).Append("themes").ToHashSet(StringComparer.OrdinalIgnoreCase);

        var source = FindRepoDir("QuickMail");
        var pattern = new System.Text.RegularExpressions.Regex(@"Path\.Combine\((?:profile\.ProfileDir|profileDir|ProfileDir)\s*,\s*""([^""]+)""");
        var written = Directory.GetFiles(Path.Combine(source, "Services"), "*.cs")
            .Append(Path.Combine(source, "App.xaml.cs"))
            .SelectMany(f => pattern.Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.NotEmpty(written);
        foreach (var name in written)
            Assert.True(carried.Contains(name) || excluded.Contains(name),
                $"The profile file \"{name}\" is neither carried by a settings category nor listed as left behind (#507).");
    }

    private static string FindRepoDir(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, name, "Services")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, name);
    }

    // ── Restart ────────────────────────────────────────────────────────────────

    [Fact]
    public void Restart_PassesTheSameArgumentsAndAWaitForThisProcess()
    {
        var args = AppRestart.BuildArguments(["--profileDir", @"C:\p", "/debug", "--waitForExit", "12", "--startup"], 345);
        Assert.Equal(["--profileDir", @"C:\p", "/debug", "--waitForExit", "345"], args);
        Assert.Equal(345, AppRestart.ParseWaitPid([.. args]));
        Assert.Equal(["--profileDir", @"C:\p", "/debug"], AppRestart.StripWaitArgument([.. args]));
    }

    [Fact]
    public void Restart_NoWaitArgumentMeansNoWait()
    {
        Assert.Null(AppRestart.ParseWaitPid(["--profileDir", "x"]));
        Assert.Null(AppRestart.ParseWaitPid(["--waitForExit"]));
        AppRestart.WaitForPreviousInstance(["--waitForExit", "999999999"]);   // a process that is not there: returns at once
    }
}

/// <summary>
/// Settings export and import are behind AllowSettingsTransfer, off until the user turns it on in
/// Settings, Advanced (#507): with it off there is no command, no menu item and no way to start
/// either; turning it on takes effect at once, and an import never changes it.
/// </summary>
public sealed class SettingsTransferSettingTests
{
    private static (MainViewModel vm, CommandRegistry registry) MakeVm()
    {
        var registry = new CommandRegistry();
        var vm = new MainViewModel(new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
            new StubConfigService(), registry, new StubViewService(),
            new StubRuleService(), new StubSmtpService());
        return (vm, registry);
    }

    [Fact]
    public void DefaultsToOffAndRoundTrips()
    {
        Assert.False(new ConfigModel().AllowSettingsTransfer);
        var text = ConfigService.BuildFileText(new ConfigModel { AllowSettingsTransfer = true });
        Assert.True(ConfigService.ParseFile(text.Split('\n')).AllowSettingsTransfer);
        Assert.False(ConfigService.ParseFile(ConfigService.BuildFileText(new ConfigModel()).Split('\n')).AllowSettingsTransfer);
    }

    [Fact]
    public void Off_OffersNothingAndStartsNothing()
    {
        var (vm, registry) = MakeVm();
        vm.ApplySettingsTransferSetting(false);
        var asked = 0;
        vm.ExportSettingsRequested += (_, _) => asked++;
        vm.ImportSettingsRequested += (_, _) => asked++;

        vm.RequestExportSettings();
        vm.RequestImportSettings();

        Assert.False(vm.IsSettingsTransferEnabled);
        Assert.DoesNotContain(registry.GetAll(), c => c.Id is "settings.export" or "settings.import");
        Assert.Equal(0, asked);
    }

    [Fact]
    public void On_RegistersBothCommandsAndTurningOffRemovesThem()
    {
        var (vm, registry) = MakeVm();
        var asked = 0;
        vm.ExportSettingsRequested += (_, _) => asked++;

        vm.ApplySettingsTransferSetting(true);
        Assert.True(vm.IsSettingsTransferEnabled);
        var export = registry.GetAll().Single(c => c.Id == "settings.export");
        Assert.Equal("Settings", export.Category);
        Assert.Contains(registry.GetAll(), c => c.Id == "settings.import");
        export.Execute();
        Assert.Equal(1, asked);

        vm.ApplySettingsTransferSetting(true);   // applied again on every Settings save: no duplicate
        Assert.Single(registry.GetAll(), c => c.Id == "settings.export");

        vm.ApplySettingsTransferSetting(false);
        Assert.DoesNotContain(registry.GetAll(), c => c.Id is "settings.export" or "settings.import");
    }

    [Fact]
    public void AnImportNeverSwitchesItOnOrOff()
    {
        Assert.Contains(nameof(ConfigModel.AllowSettingsTransfer), SettingsConfigTransfer.Local);
        var local = new ConfigModel { AllowSettingsTransfer = true };
        SettingsConfigTransfer.Apply(new ConfigModel { AllowSettingsTransfer = false }, local, []);
        Assert.True(local.AllowSettingsTransfer);
    }
}
