using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Tests;

/// <summary>
/// A MainViewModel with two connected accounts, Work and Home, with known folders, for the favorites
/// (#1056) and folder tree arrangement (#1058) tests. Modelled on HiddenFoldersTests' fixture.
/// </summary>
internal static class FolderTreeFixture
{
    public static readonly Guid Work = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid Home = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>Records every save, with the account order it was given.</summary>
    public sealed class RecordingAccountService : IAccountService
    {
        public int Saves { get; private set; }
        public List<AccountModel> LastSaved { get; private set; } = [];
        /// <summary>What LoadAccounts returns, standing in for accounts.json after Manage Accounts.</summary>
        public List<AccountModel>? ToLoad { get; set; }
        public List<AccountModel> LoadAccounts() => ToLoad is { } list ? [.. list] : [];
        public void SaveAccounts(List<AccountModel> accounts)
        {
            Saves++;
            LastSaved = [.. accounts];
        }
        public void SetDefaultAccount(Guid accountId) { }
    }

    public sealed class ViewsStub(params SavedView[] views) : IViewService
    {
        public List<SavedView> Load() => [.. views];
        public void Save(List<SavedView> v) { }
    }

    public static MailFolderModel F(string fullName, string display, Guid accountId,
                                    SpecialFolderKind kind = SpecialFolderKind.None)
        => new() { FullName = fullName, DisplayName = display, Kind = kind, AccountId = accountId };

    public sealed record Made(MainViewModel Vm, RecordingAccountService Accounts, StubConfigService Config);

    public static async Task<Made> MakeAsync(
        bool withCalendar = true, IViewService? views = null, StubConfigService? config = null,
        Action<AccountModel, AccountModel>? arrange = null, bool connectHome = true)
    {
        var folders = new Dictionary<Guid, List<MailFolderModel>>
        {
            [Work] =
            [
                F("INBOX", "Inbox", Work, SpecialFolderKind.Inbox),
                F("Archive", "Archive", Work),
                F("Archive/2020", "2020", Work),
                F("Archives", "Archives", Work),
                F("Lists", "Lists", Work),
                F("Notes", "Notes", Work),
            ],
        };
        if (connectHome)
            folders[Home] =
            [
                F("INBOX", "Inbox", Home, SpecialFolderKind.Inbox),
                F("Notes", "Notes", Home),
            ];

        var accounts = new RecordingAccountService();
        config ??= new StubConfigService();
        var vm = new MainViewModel(
            new FolderedMailService(folders, []), accounts, new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(),
            config, new StubCommandRegistry(), views ?? new StubViewService(),
            new StubRuleService(), new StubSmtpService(),
            calendarService: withCalendar ? new StubCalendarService() : null);

        var work = new AccountModel { Id = Work, AccountName = "Work", Username = "work@example.com", AuthType = AuthType.OAuth2Microsoft };
        var home = new AccountModel { Id = Home, AccountName = "Home", Username = "home@example.com", AuthType = AuthType.OAuth2Microsoft };
        arrange?.Invoke(work, home);
        vm.Accounts.Add(work);
        vm.Accounts.Add(home);
        await vm.ConnectAllAccountsAsync();
        vm.UpdateSavedViews();
        return new Made(vm, accounts, config);
    }

    public static IEnumerable<FolderTreeNode> Flatten(IEnumerable<FolderTreeNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in Flatten(n.Children)) yield return c;
        }
    }

    /// <summary>The folder's node under its own account (not a favorite or a view's copy).</summary>
    public static FolderTreeNode NodeFor(MainViewModel vm, Guid accountId, string fullName) =>
        Flatten(vm.FolderTree!.Single(r => r.IsHeader && r.AccountId == accountId).Children)
            .Single(n => n.Folder is { } f && f.AccountId == accountId && f.FullName == fullName);

    public static FolderTreeNode Root(MainViewModel vm, string label) =>
        vm.FolderTree!.Single(r => r.Label == label);

    public static FolderTreeNode? FavoritesHeader(MainViewModel vm) =>
        vm.FolderTree!.SingleOrDefault(r => r.SectionKey == FolderTreeOrder.Favorites);

    public static List<string> RootLabels(MainViewModel vm) => [.. vm.FolderTree!.Select(r => r.Label)];

    public static List<string> FavoriteLabels(MainViewModel vm) =>
        FavoritesHeader(vm) is { } header ? [.. header.Children.Select(c => c.Label)] : [];
}
