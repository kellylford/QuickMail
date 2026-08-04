using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The other half of <see cref="RuleTargetPickerTests"/>, which all start from
/// <c>ForRuleTarget</c> and so would stay green if the rule editors went back to constructing a flat
/// <c>FolderPickerWindow</c> themselves — precisely the state this change fixed.
///
/// <para>Neither editor can be stood up without a live rules ViewModel and an owner window, so this
/// reads the source instead. Same shape as <see cref="FolderMoveCopyCallSiteTests"/>.</para>
/// </summary>
public class RuleTargetPickerCallSiteTests
{
    [Theory]
    [InlineData("Views/RulesManagerWindow.xaml.cs")]
    [InlineData("Views/ServerRuleEditorWindow.xaml.cs")]
    public void TheRuleEditorsBuildTheirFolderPickerThroughForRuleTarget(string file)
    {
        var body = MethodBody(file, "OnPickFolderRequested");

        Assert.Contains("FolderPickerWindow.ForRuleTarget", body, StringComparison.Ordinal);
        // Whitespace-tolerant: "new  FolderPickerWindow (" is the same reversion.
        Assert.False(Regex.IsMatch(body, @"new\s+FolderPickerWindow\s*\("),
                     $"{file} constructs a FolderPickerWindow directly — that is the flat list again.");
    }

    /// <summary>
    /// The account argument is what keeps a rule from being pointed at another mailbox's folder of
    /// the same name, and it is invisible to the presentation tests: passing null would still produce
    /// a tree, just an unscoped one. So pin that each editor passes something account-shaped rather
    /// than a literal null.
    /// </summary>
    [Theory]
    [InlineData("Views/RulesManagerWindow.xaml.cs", "accountId")]
    [InlineData("Views/ServerRuleEditorWindow.xaml.cs", "_ruleAccountId")]
    public void TheRuleEditorsScopeThePickerToTheRulesAccount(string file, string accountArgument)
    {
        var body = MethodBody(file, "OnPickFolderRequested");

        var call = Regex.Match(body, @"ForRuleTarget\s*\((?<args>[^;]*?)\)\s*;", RegexOptions.Singleline);
        Assert.True(call.Success, $"could not find the ForRuleTarget call in {file}.");
        Assert.Contains(accountArgument, call.Groups["args"].Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Text of one method, from its signature to the first closing brace at the same indentation.
    /// Crude, but these files' braces are consistently indented, and a miss fails loudly rather than
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
