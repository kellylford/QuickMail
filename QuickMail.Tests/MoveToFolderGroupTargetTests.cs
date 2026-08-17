using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Issue #566: Move to Folder did nothing when the selection was a conversation, not a message.
/// Selecting a group header does not update <c>MainViewModel.SelectedMessage</c>
/// (<c>GroupedMessageTreeController.OnSelectedItemChanged</c>), so a command that reads only the
/// selected message either files the message the user had selected before arrowing onto the header —
/// silently the wrong one — or, with none selected, is never even available. Both failure modes are
/// invisible: the wrong-message one leaves a picker looking perfectly normal.
///
/// <para>Neither the handlers nor the registrations can be exercised without a shown
/// <c>MainWindow</c> and a realized <c>TreeView</c> container (WPF's <c>TreeView.SelectedItem</c> is
/// read-only; only a live container can set it), and the handlers then block on a modal picker. So
/// this reads the source, as <see cref="FolderMoveCopyCallSiteTests"/> does for the same reason:
/// dropping the group branch, or putting the availability gate back on the selected message alone,
/// fails here.</para>
///
/// <para>Plain <c>[Fact]</c>/<c>[Theory]</c> — text and regex, no WPF — so it stays out of the
/// <c>WpfTests</c> collection.</para>
/// </summary>
public class MoveToFolderGroupTargetTests
{
    private const string MainWindowSource = "Views/MainWindow.xaml.cs";

    /// <summary>Both file actions must resolve a selected group header before falling back.</summary>
    [Theory]
    [InlineData("MoveMessageToFolderAsync")]
    [InlineData("CopyMessageToFolderAsync")]
    public void FilingAMessage_ResolvesASelectedGroupHeaderFirst(string method)
    {
        var body = MethodBody(MainWindowSource, method);

        Assert.Contains("SelectedGroupTarget()", body, StringComparison.Ordinal);
        Assert.Contains("GetSelectedMessages()", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole point of the fix: the hotkey and the palette must offer the command when a group
    /// header is selected. Gating on the selected message alone is what made the hotkey do nothing.
    /// </summary>
    [Theory]
    [InlineData("mail.moveToFolder")]
    [InlineData("mail.copyToFolder")]
    public void TheFileCommandsAreAvailableOnAGroupHeader(string commandId)
    {
        var registration = RegistrationOf(MainWindowSource, commandId);

        Assert.Contains("isAvailable: CanFileSelection", registration, StringComparison.Ordinal);
    }

    /// <summary>
    /// Availability and target resolution have to agree, and both have to cover every grouped view —
    /// a fix that only reached Conversations would leave From and To exactly as #566 found them.
    /// </summary>
    [Fact]
    public void GroupTargetingCoversAllThreeGroupedViews()
    {
        foreach (var method in new[] { "SelectedGroupTarget", "IsGroupRowSelected" })
        {
            var body = MethodBody(MainWindowSource, method);
            foreach (var tree in new[] { "ConversationTree", "SenderGroupTree", "ToGroupTree" })
                Assert.Contains(tree, body, StringComparison.Ordinal);
        }

        Assert.Contains("IsGroupRowSelected()", MethodBody(MainWindowSource, "CanFileSelection"),
                        StringComparison.Ordinal);
    }

    /// <summary>The text of one <c>_registry.Register</c> call, found by the command id it declares.</summary>
    private static string RegistrationOf(string relativePath, string commandId)
    {
        var source = ReadSource(relativePath);
        var idAt   = source.IndexOf($"id: \"{commandId}\"", StringComparison.Ordinal);
        Assert.True(idAt >= 0, $"{commandId} is not registered in {relativePath} — renamed or removed?");

        var end = source.IndexOf("));", idAt, StringComparison.Ordinal);
        Assert.True(end > idAt, $"could not find the end of the {commandId} registration.");

        return source.Substring(idAt, end - idAt);
    }

    /// <summary>
    /// Text of one method, from its declaration to the first closing brace at the same indentation
    /// (or, for an expression body, to the first semicolon). Crude, but this file's braces are
    /// consistently indented, and a miss fails loudly rather than silently matching the wrong text.
    /// <para>The declaration is matched on its access modifier, not on the name alone: these methods
    /// are also named inside the command registrations above them, and matching a call site would
    /// silently read the wrong block of source.</para>
    /// </summary>
    private static string MethodBody(string relativePath, string methodName)
    {
        var source = ReadSource(relativePath);
        var start  = Regex.Match(
            source,
            $@"^(?<indent>[ \t]*)(private|public|internal|protected)[^\r\n]*\b{Regex.Escape(methodName)}\s*\(",
            RegexOptions.Multiline);
        Assert.True(start.Success, $"{methodName} not found in {relativePath} — renamed or removed?");

        var rest = source[start.Index..];
        var end  = rest.Contains("=>", StringComparison.Ordinal)
                   && rest.IndexOf("=>", StringComparison.Ordinal) < FirstBraceOrEnd(rest)
            ? Regex.Match(rest, @";")                                   // expression body
            : Regex.Match(rest, $@"^{start.Groups["indent"].Value}}}", RegexOptions.Multiline);
        Assert.True(end.Success, $"could not find the end of {methodName} in {relativePath}.");

        return rest[..(end.Index + end.Length)];
    }

    private static int FirstBraceOrEnd(string text)
    {
        var i = text.IndexOf('{');
        return i < 0 ? text.Length : i;
    }

    private static string ReadSource(string relativePath)
    {
        var path = Path.Combine(RepoRoot(), "QuickMail", relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"{path} not found.");
        return File.ReadAllText(path);
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
