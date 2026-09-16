using System;
using System.ComponentModel;
using System.IO;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The rule editor's Move to folder / Copy to folder buttons must say which folder is chosen.
///
/// <para>Reported by ear: after choosing a folder — and again when reopening a saved rule — the button still
/// announced "Choose move-to folder", as though the folder had been lost, though activating it landed on the
/// folder the rule already used. The cause was a fixed <c>AutomationProperties.Name</c> on each button, which
/// overrides the button's text: the text said "Kept" while the name said "Choose move-to folder". The rendered
/// window was correct, so every sighted check passed.</para>
/// </summary>
public class RuleEditorFolderButtonTests
{
    [Fact]
    public void WithNoFolderChosen_TheButtonsSayWhatTheyAreFor()
    {
        var vm = ServerRuleEditorViewModel.ForNew();

        Assert.Equal("Choose move-to folder", vm.MoveToFolderButtonName);
        Assert.Equal("Choose copy-to folder", vm.CopyToFolderButtonName);
    }

    [Fact]
    public void AChosenFolder_IsPartOfTheButtonsName()
    {
        var vm = ServerRuleEditorViewModel.ForNew();
        vm.MoveToFolderName = "Digests";
        vm.CopyToFolderName = "Kept";

        // The purpose stays in the name: "Digests" alone would not say which of the two buttons this is.
        Assert.Equal("Move to folder: Digests", vm.MoveToFolderButtonName);
        Assert.Equal("Copy to folder: Kept", vm.CopyToFolderButtonName);
    }

    [Fact]
    public void ChoosingAFolder_RenamesTheButtonThereAndThen()
    {
        // Without the change notification the name would not be re-read after the folder picker closes, which is
        // the moment the person needs to hear what they just chose.
        var vm = ServerRuleEditorViewModel.ForNew();
        var renamed = 0;
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ServerRuleEditorViewModel.MoveToFolderButtonName)
                or nameof(ServerRuleEditorViewModel.CopyToFolderButtonName)) renamed++;
        };

        vm.MoveToFolderName = "Digests";
        vm.CopyToFolderName = "Kept";

        Assert.Equal(2, renamed);
    }

    [Fact]
    public void ReopeningASavedRule_TheButtonCarriesItsFolder()
    {
        // The reported case: a rule saved with a folder, opened again from the rules list.
        var saved = new MailRule
        {
            Name = "File digests",
            SubjectContains = "digest",
            Action = RuleAction.MoveToFolder,
            Actions = [RuleAction.CopyToFolder, RuleAction.MoveToFolder],
            TargetFolder = "INBOX/Digests",
            CopyTargetFolder = "INBOX/Kept",
        };

        var vm = ServerRuleEditorViewModel.ForEditClient(saved);

        Assert.Equal("Move to folder: INBOX/Digests", vm.MoveToFolderButtonName);
        Assert.Equal("Copy to folder: INBOX/Kept", vm.CopyToFolderButtonName);
    }

    [Fact]
    public void TheButtonsTakeTheirNameFromTheViewModel_NotAFixedString()
    {
        // Pinned from the source: reading the real name means showing the window, which takes focus from whoever
        // is running the tests. A fixed name here is the whole defect, so guard against one coming back.
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "Views", "ServerRuleEditorWindow.xaml"));

        Assert.Contains("AutomationProperties.Name=\"{Binding MoveToFolderButtonName}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{Binding CopyToFolderButtonName}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AutomationProperties.Name=\"Choose move-to folder\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AutomationProperties.Name=\"Choose copy-to folder\"", xaml, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "QuickMail", "Views")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Couldn't find the repository root above the test output.");
    }
}
