using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Which account the Rules Manager opens on (#550 follow-up), decided by the view the user is in.
///
/// <para>The rules window falls back to the default account when it is given no account, and it is
/// given <see cref="MainViewModel.RulesAccountContext"/>. <c>SelectedAccount</c> cannot answer this on
/// its own: choosing a virtual folder never updates it, so it still holds whichever account was last
/// visited. Every test here therefore makes the selected account DIFFERENT from the account the view
/// belongs to. An earlier version used the same id for both, and so passed while the app opened on the
/// wrong account.</para>
/// </summary>
public class RulesAccountContextTests
{
    private static readonly Guid Work = Guid.NewGuid();   // the account the view belongs to
    private static readonly Guid Home = Guid.NewGuid();   // the account last visited: SelectedAccount

    private static SavedView View(params Guid[] accounts) => new()
    {
        Name = "View",
        Folders = accounts.Select((a, i) => new ViewFolder { AccountId = a, FolderFullName = "Folder" + i }).ToList(),
    };

    private static MailFolderModel SelectedAs(SavedView view, bool allFolders) => new()
    {
        FullName = (allFolders ? MainViewModel.ViewAllPrefix : MainViewModel.ViewPrefix) + view.Id,
        DisplayName = view.Name,
    };

    [Fact]
    public void AViewThatSpansAccounts_GivesNoAccount()
        => Assert.Null(MainViewModel.AccountContextForRules(MainViewModel.AllInboxesFolder, Home, []));

    [Fact]
    public void ARealFolder_GivesItsOwnAccount()
    {
        var inbox = new MailFolderModel { FullName = "INBOX", DisplayName = "Inbox", AccountId = Work };

        Assert.Equal(Work, MainViewModel.AccountContextForRules(inbox, Home, []));
    }

    [Theory]
    [InlineData(true)]    // the model carries the account id…
    [InlineData(false)]   // …and when it does not, the sentinel still names the account
    public void APerAccountAllMail_GivesItsOwnAccount_NotTheOneLastVisited(bool accountIdOnModel)
    {
        var allMail = new MailFolderModel
        {
            FullName = MainViewModel.AccountMailPrefix + Work,
            DisplayName = "All Mail",
            AccountId = accountIdOnModel ? Work : Guid.Empty,
        };

        Assert.Equal(Work, MainViewModel.AccountContextForRules(allMail, Home, []));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASavedViewOnOneAccount_GivesThatAccount(bool allFolders)
    {
        var view = View(Work, Work);

        Assert.Equal(Work, MainViewModel.AccountContextForRules(SelectedAs(view, allFolders), Home, [view]));
    }

    [Fact]
    public void ASavedViewAcrossAccounts_GivesNoAccount()
    {
        var view = View(Work, Home);

        Assert.Null(MainViewModel.AccountContextForRules(SelectedAs(view, allFolders: false), Home, [view]));
    }

    [Fact]
    public void ASavedViewThatNoLongerExists_GivesNoAccount()
    {
        var gone = View(Work);

        Assert.Null(MainViewModel.AccountContextForRules(SelectedAs(gone, allFolders: false), Home, []));
    }

    [Fact]
    public void NoFolderSelected_GivesTheSelectedAccount()
        => Assert.Equal(Home, MainViewModel.AccountContextForRules(null, Home, []));

    /// <summary>
    /// The wiring. A correct decision is no use if the window that builds the rules view model hands it
    /// <c>SelectedAccount</c> instead. Read from source, the same way
    /// <see cref="RuleTargetPickerCallSiteTests"/> checks the rule editors' wiring.
    /// </summary>
    [Fact]
    public void MainWindow_HandsTheRulesWindowTheRulesAccountContext()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "Views", "MainWindow.xaml.cs"));
        var construction = Regex.Match(
            source, @"new\s+UnifiedRulesViewModel\s*\((?<args>[^;]*?)\)\s*;", RegexOptions.Singleline);
        Assert.True(construction.Success, "MainWindow no longer constructs a UnifiedRulesViewModel.");

        var args = construction.Groups["args"].Value;
        Assert.Contains("_vm.RulesAccountContext", args, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedAccount?.Id", args, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of the wiring. Every behaviour test above calls the pure function with the saved
    /// views it is given; if the instance property handed it an empty list instead, all of them would
    /// still pass while one-account saved views dropped to the default again.
    /// </summary>
    [Fact]
    public void RulesAccountContext_PassesTheSavedViews()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "ViewModels", "MainViewModel.cs"));

        Assert.Contains("AccountContextForRules(SelectedFolder, SelectedAccount?.Id, SavedViews)", source,
                        StringComparison.Ordinal);
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
