// Issue #859: the two credential partials are gitignored, so a fresh clone could not build until
// someone ran scripts/write-credential-partials.ps1 by hand. QuickMail.csproj now writes empty
// placeholders for whichever is missing. These checks guard the two properties that matter: it
// runs before the build, and it never overwrites a file that exists (a release build writes the
// real values first, and overwriting them would ship an app whose Google sign-in and bug reports
// silently fail).

using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace QuickMail.Tests;

public class CredentialPlaceholderTargetTests
{
    private static readonly string[] PartialNames =
        ["GoogleOAuthService.Credentials.cs", "BugReportService.Credentials.cs"];

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "scripts", "write-credential-partials.ps1")))
                return dir.FullName;
        throw new InvalidOperationException("Repo source tree not found from " + AppContext.BaseDirectory + ".");
    }

    private static XElement Target()
    {
        var csproj = XDocument.Load(Path.Combine(RepoRoot(), "QuickMail", "QuickMail.csproj"));
        var target = csproj.Descendants("Target")
            .SingleOrDefault(t => (string?)t.Attribute("Name") == "WriteCredentialPlaceholders");
        Assert.NotNull(target);
        return target;
    }

    [Fact]
    public void Target_RunsBeforeTheBuild_OnlyWhenAPartialIsMissing()
    {
        var target = Target();
        Assert.Contains("BeforeBuild", (string?)target.Attribute("BeforeTargets") ?? "");

        var condition = (string?)target.Attribute("Condition") ?? "";
        foreach (var name in PartialNames)
            Assert.Contains($"!Exists('$(MSBuildProjectDirectory)/Services/{name}')", condition);
    }

    [Fact]
    public void EveryWrite_IsConditionedOnItsOwnFileNotExisting()
    {
        var writes = Target().Descendants("WriteLinesToFile").ToList();
        Assert.Equal(PartialNames.Length, writes.Count);
        foreach (var write in writes)
        {
            var file = (string?)write.Attribute("File");
            Assert.False(string.IsNullOrEmpty(file));
            Assert.Equal($"!Exists('{file}')", (string?)write.Attribute("Condition"));
        }
    }

    [Fact]
    public void Placeholders_DeclareTheSameConstantsAsTheScript()
    {
        // The script is the other writer of these files; both must declare the same constants or
        // a fresh clone and CI would compile different code.
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "write-credential-partials.ps1"));
        var lines = Target().Descendants()
            .Where(e => e.Name.LocalName is "_QmGoogleLines" or "_QmBugReportLines")
            .Select(e => Uri.UnescapeDataString((string?)e.Attribute("Include") ?? ""))
            .Where(l => l.TrimStart().StartsWith("private const string", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(4, lines.Count);
        foreach (var line in lines)
        {
            Assert.EndsWith("\"\";", line);  // empty placeholder, never a value
            var declaration = line[..line.IndexOf('"')];  // e.g. "    private const string ClientId     = "
            Assert.Contains(declaration, script);
        }
    }

    [Fact]
    public void Csproj_ReferencesNoSecrets()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "QuickMail.csproj"));
        Assert.DoesNotContain("secrets.", csproj, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GOOGLE_CLIENT", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("BUG_REPORT_RELAY", csproj, StringComparison.Ordinal);
    }
}
