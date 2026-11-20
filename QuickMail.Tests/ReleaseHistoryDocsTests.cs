using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The published release history (scripts/build-release-history.py) turns every
/// docs/release-notes-vX.Y.Z.md into a page of its own, titled from the file's first
/// heading and reached from the User Guide's Release History section. Neither the heading
/// nor that section is exercised by anything else: get the heading wrong and the page for
/// one version announces another (two files said "v0.6" and "v0.64" for months), and drop
/// the guide section and the history is still published but no longer linked from the
/// guide, which is the state issue #556 showed can last for months unnoticed.
/// </summary>
public class ReleaseHistoryDocsTests
{
    [Fact]
    public void EveryReleaseNotesFile_IsTitledWithItsOwnVersion()
    {
        var docs = Path.Combine(RepoRoot(), "docs");
        var files = Directory.GetFiles(docs, "release-notes-v*.md");

        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var version = Regex.Match(Path.GetFileName(file), @"^release-notes-v(.+)\.md$")
                               .Groups[1].Value;
            var title = File.ReadLines(file).First();

            Assert.True(title.StartsWith("# ", StringComparison.Ordinal),
                $"{Path.GetFileName(file)} must open with a '# ' heading; it opens with '{title}'.");
            Assert.True(title.Contains($"v{version}", StringComparison.Ordinal),
                $"{Path.GetFileName(file)} is titled '{title}', which does not name v{version}. " +
                 "The generated page takes its title from this line, so it would announce the " +
                 "wrong version.");
        }
    }

    [Fact]
    public void UserGuide_HasAReleaseHistorySectionPointingAtThePublishedIndex()
    {
        var guide = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "USER-GUIDE.md"));

        Assert.Contains("## Release History", guide, StringComparison.Ordinal);
        Assert.Contains("https://kellylford.github.io/QuickMail/releases.html", guide,
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
