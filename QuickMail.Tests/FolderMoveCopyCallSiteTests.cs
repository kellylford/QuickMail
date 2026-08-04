using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Issue #431 was not a bug inside <c>FolderPickerWindow</c> — the tree presentation already existed
/// and worked. It was that the folder tree's "Move Folder…" and "Copy Folder…" commands asked for the
/// flat one. <see cref="FolderPickerTreeTests"/> all start from <c>ForFolderMoveCopy</c>, so every one
/// of them stays green if those two handlers go back to constructing a flat picker directly, which is
/// precisely the reported bug.
///
/// <para>Neither handler can be invoked without a real <c>MainWindow</c> and a live
/// <c>MainViewModel</c>, so this reads the source instead: each must build its picker through the
/// factory and must not construct a <c>FolderPickerWindow</c> itself. Deleting a handler, renaming it,
/// or switching it back to the flat constructor all fail here.</para>
///
/// <para>Plain <c>[Theory]</c> — text and regex, no WPF — so it is deliberately kept out of the
/// <c>WpfTests</c> collection, which exists to serialize window-loading tests and is disturbed by
/// non-STA tests scheduled inside it.</para>
/// </summary>
public class FolderMoveCopyCallSiteTests
{
    [Theory]
    [InlineData("FolderContextMenu_MoveFolder_Click")]
    [InlineData("FolderContextMenu_CopyFolder_Click")]
    public void TheFolderMoveAndCopyCommands_BuildTheirPickerThroughForFolderMoveCopy(string handler)
    {
        var body = MethodBody("Views/MainWindow.xaml.cs", handler);

        Assert.Contains("FolderPickerWindow.ForFolderMoveCopy", body, StringComparison.Ordinal);
        // Whitespace-tolerant: "new  FolderPickerWindow (" is the same reversion.
        Assert.False(Regex.IsMatch(body, @"new\s+FolderPickerWindow\s*\("),
                     $"{handler} constructs a FolderPickerWindow directly — that is the flat list #431 reported.");
    }

    /// <summary>
    /// Text of one method, from its signature to the first closing brace at the same indentation.
    /// Crude, but this file's braces are consistently indented, and a miss fails loudly rather than
    /// silently matching the wrong text.
    /// </summary>
    private static string MethodBody(string relativePath, string methodName)
    {
        var path = Path.Combine(RepoRoot(), "QuickMail", relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"{path} not found.");

        var source = File.ReadAllText(path);
        var start  = Regex.Match(
            source, $@"^(?<indent>[ \t]*)\S[^\r\n]*\b{Regex.Escape(methodName)}\s*\(", RegexOptions.Multiline);
        Assert.True(start.Success, $"{methodName} not found in {relativePath} — renamed or removed?");

        var end = Regex.Match(
            source[start.Index..], $@"^{start.Groups["indent"].Value}}}", RegexOptions.Multiline);
        Assert.True(end.Success, $"could not find the end of {methodName} in {relativePath}.");

        return source.Substring(start.Index, end.Index + end.Length);
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
