using System;
using System.IO;
using System.Text.RegularExpressions;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Which account the Rules Manager opens on (#550 follow-up).
///
/// <para>The rules window falls back to the default account when it is given no account. It is given
/// <see cref="MainViewModel.RulesAccountContext"/>, and that is the piece that makes the fallback
/// reachable: <c>SelectedAccount</c> alone never becomes null on All Inboxes, because choosing a virtual
/// folder leaves it on whichever account was last visited. A first cut changed only the rules view model
/// and passed tests that fed it null directly — a state the running app almost never produced.</para>
/// </summary>
public class RulesAccountContextTests
{
    private static readonly Guid Visited = Guid.NewGuid();

    [Fact]
    public void AViewThatSpansAccounts_GivesNoAccount()
        => Assert.Null(MainViewModel.AccountContextForRules(MainViewModel.AllInboxesFolder, Visited));

    [Fact]
    public void ARealFolder_GivesTheAccountYouAreIn()
    {
        var inbox = new MailFolderModel { FullName = "INBOX", DisplayName = "Inbox", AccountId = Visited };

        Assert.Equal(Visited, MainViewModel.AccountContextForRules(inbox, Visited));
    }

    [Fact]
    public void APerAccountAllMail_KeepsItsAccount()
    {
        // Virtual, but it belongs to one account, so the Rules Manager should open on that account rather
        // than jump to the default.
        var allMail = new MailFolderModel
        {
            FullName = MainViewModel.AccountMailPrefix + Visited, DisplayName = "All Mail", AccountId = Visited,
        };

        Assert.Equal(Visited, MainViewModel.AccountContextForRules(allMail, Visited));
    }

    [Fact]
    public void NoFolderSelected_GivesTheAccountYouAreIn()
        => Assert.Equal(Visited, MainViewModel.AccountContextForRules(null, Visited));

    /// <summary>
    /// The wiring the first cut missed. A correct view model is no use if the window that builds it hands
    /// it <c>SelectedAccount</c>, which All Inboxes never clears. Read from source, the same way
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
