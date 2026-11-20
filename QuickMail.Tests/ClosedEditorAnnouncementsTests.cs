using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// #701: a rule editor's messages are said once — by the editor while it is open, and by the Rules Manager once
/// it has closed. The second half is for saving a server-side rule (a work or school Microsoft 365 account), which
/// can still be waiting on the server when the editor is closed: the editor stops listening as it closes, so without the hand-over a failure then
/// was announced by nothing. (That the Rules Manager says nothing while the editor is open is pinned in
/// UnifiedRulesViewModelTests.EditorMessages_AreTheEditorWindowsToAnnounce_NotPassedOnByTheRulesManager.)
/// </summary>
public class ClosedEditorAnnouncementsTests
{
    // A save with no name is refused, and the refusal is something the editor announces.
    private static Task RefusedSave(ServerRuleEditorViewModel editor) => editor.SaveCommand.ExecuteAsync(null);

    [Fact]
    public async Task OnceTheEditorHasClosed_TheRulesManagerSaysItsMessage_Once()
    {
        var heard = new List<(string Text, AnnouncementCategory Category)>();
        var handOver = new ClosedEditorAnnouncements((t, c) => heard.Add((t, c)));
        var editor = ServerRuleEditorViewModel.ForNew();

        handOver.EditorClosed(editor);
        await RefusedSave(editor);

        var said = Assert.Single(heard);
        Assert.Equal(AnnouncementCategory.Result, said.Category);
        Assert.False(string.IsNullOrWhiteSpace(said.Text));
    }

    [Fact]
    public async Task AfterTheRulesManagerHasClosed_NothingIsSaid()
    {
        var heard = new List<string>();
        var handOver = new ClosedEditorAnnouncements((t, _) => heard.Add(t));
        var closedEarlier = ServerRuleEditorViewModel.ForNew();
        var closedAfterIt = ServerRuleEditorViewModel.ForNew();   // an owned editor whose Closed arrives late
        var raised = 0;
        closedEarlier.AnnouncementRequested += (_, _) => raised++;
        closedAfterIt.AnnouncementRequested += (_, _) => raised++;
        handOver.EditorClosed(closedEarlier);

        handOver.Stop();
        handOver.EditorClosed(closedAfterIt);
        await RefusedSave(closedEarlier);
        await RefusedSave(closedAfterIt);

        Assert.Equal(2, raised);   // both editors did speak, so the empty list is not just silence
        Assert.Empty(heard);
    }

    [Fact]
    public void TheRulesManagerWindow_HandsAClosedEditorOver_AndStopsWhenItCloses()
    {
        // Pinned from the source: doing it for real means opening the editor window, which activates it and takes
        // focus from whatever the person running the tests is doing.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "Views", "UnifiedRulesWindow.xaml.cs"));

        Assert.Contains("new ClosedEditorAnnouncements(OnAnnouncement)", source, StringComparison.Ordinal);
        Assert.Contains("editor.Closed += (_, _) => _closedEditors.EditorClosed(editorVm);", source, StringComparison.Ordinal);
        Assert.Contains("_closedEditors.Stop();", source, StringComparison.Ordinal);
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
