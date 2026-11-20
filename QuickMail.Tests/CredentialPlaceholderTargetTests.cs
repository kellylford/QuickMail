// Issue #859: the two credential partials are gitignored, so a fresh clone could not build until
// someone ran scripts/write-credential-partials.ps1 by hand. QuickMail.csproj now writes empty
// placeholders for whichever is missing. These checks guard what matters:
//   - it never overwrites a file that exists (a release build writes the real values first, and
//     overwriting them would ship an app whose Google sign-in and bug reports silently fail);
//   - it adds only the file it wrote to Compile, so a file that already existed is not compiled
//     twice (CS2002, an error under -warnaserror);
//   - on GitHub Actions it does not write at all and the build fails instead, so a release job
//     that lost its script step still fails loudly;
//   - its placeholder text is exactly what the script writes without -Release.
// The quickmail.yml fresh-clone-build job exercises the target itself.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace QuickMail.Tests;

public class CredentialPlaceholderTargetTests
{
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "scripts", "write-credential-partials.ps1")))
                return dir.FullName;
        throw new InvalidOperationException("Repo source tree not found from " + AppContext.BaseDirectory + ".");
    }

    private static XDocument Csproj() => XDocument.Load(Path.Combine(RepoRoot(), "QuickMail", "QuickMail.csproj"));

    private static XElement TargetNamed(string name)
    {
        var target = Csproj().Descendants("Target").SingleOrDefault(t => (string?)t.Attribute("Name") == name);
        Assert.NotNull(target);
        return target;
    }

    [Fact]
    public void WriteTarget_RunsBeforeTheBuild_OffCiAndOnlyWhenAPartialIsMissing()
    {
        var target = TargetNamed("WriteCredentialPlaceholders");
        Assert.Contains("BeforeBuild", (string?)target.Attribute("BeforeTargets") ?? "");

        var condition = (string?)target.Attribute("Condition") ?? "";
        Assert.Contains("'$(GITHUB_ACTIONS)' != 'true'", condition);
        Assert.Contains("!Exists('$(_QmGoogleCredentials)')", condition);
        Assert.Contains("!Exists('$(_QmBugReportCredentials)')", condition);
    }

    [Fact]
    public void CheckTarget_FailsTheBuildOnCi_WhenAPartialIsMissing()
    {
        var target = TargetNamed("CheckCredentialPartials");
        Assert.Contains("BeforeBuild", (string?)target.Attribute("BeforeTargets") ?? "");

        var condition = (string?)target.Attribute("Condition") ?? "";
        Assert.Contains("'$(GITHUB_ACTIONS)' == 'true'", condition);
        Assert.Contains("!Exists('$(_QmGoogleCredentials)')", condition);
        Assert.Contains("!Exists('$(_QmBugReportCredentials)')", condition);

        var error = target.Elements("Error").SingleOrDefault();
        Assert.NotNull(error);
        Assert.Contains("scripts/write-credential-partials.ps1", (string?)error.Attribute("Text") ?? "");
    }

    [Fact]
    public void EachFile_IsWrittenAndCompiled_OnlyWhenItWasMissing()
    {
        var target = TargetNamed("WriteCredentialPlaceholders");
        var flags = target.Elements("PropertyGroup").Elements().ToDictionary(e => e.Name.LocalName);

        foreach (var (flag, path, include) in new[]
                 {
                     ("_QmWriteGoogle", "$(_QmGoogleCredentials)", "Services/GoogleOAuthService.Credentials.cs"),
                     ("_QmWriteBugReport", "$(_QmBugReportCredentials)", "Services/BugReportService.Credentials.cs"),
                 })
        {
            // The flag is decided before anything is written.
            Assert.True(flags.TryGetValue(flag, out var decl), $"{flag} is not declared in the target.");
            Assert.Equal($"!Exists('{path}')", (string?)decl.Attribute("Condition"));

            var guard = $"'$({flag})' == 'true'";
            var write = target.Elements("WriteLinesToFile").Single(w => (string?)w.Attribute("File") == path);
            Assert.Equal(guard, (string?)write.Attribute("Condition"));

            var compile = target.Descendants("Compile").Single(c => (string?)c.Attribute("Include") == include);
            Assert.Equal(guard, (string?)compile.Attribute("Condition"));
        }

        // No Remove: inside a target it does not match the glob's backslash paths, which is how a
        // file that already existed ended up compiled twice.
        Assert.DoesNotContain(target.Descendants("Compile"), c => c.Attribute("Remove") != null);
    }

    [Theory]
    [InlineData("GoogleOAuthService", "_QmGoogleLines")]
    [InlineData("BugReportService", "_QmBugReportLines")]
    public void Placeholders_AreExactlyWhatTheScriptWrites(string className, string itemName)
    {
        var targetLines = TargetNamed("WriteCredentialPlaceholders").Descendants(itemName)
            .Select(e => Uri.UnescapeDataString((string?)e.Attribute("Include") ?? ""))
            .ToList();

        Assert.Equal(ScriptPlaceholder(className), targetLines);
    }

    // The script's here-string for the class, with every interpolated value blanked, which is
    // exactly what it writes without -Release.
    private static List<string> ScriptPlaceholder(string className)
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "write-credential-partials.ps1"));
        var hereStrings = Regex.Matches(script, "@\"\\r?\\n(.*?)\\r?\\n\"@", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .Where(s => s.Contains("public partial class " + className + "\n", StringComparison.Ordinal)
                     || s.Contains("public partial class " + className + "\r\n", StringComparison.Ordinal))
            .ToList();
        Assert.Single(hereStrings);

        var blanked = Regex.Replace(hereStrings[0], @"\$\(\$v\['[A-Z_]+'\]\)", "");
        Assert.DoesNotContain("$", blanked);
        return Regex.Split(blanked, "\r?\n").ToList();
    }

    [Fact]
    public void Csproj_ReferencesNoSecrets()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "QuickMail", "QuickMail.csproj"));
        Assert.DoesNotContain("secrets.", csproj, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GOOGLE_CLIENT", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("BUG_REPORT_RELAY", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflows_WriteThePartialsOnlyThroughTheScript()
    {
        // A third inline copy of the placeholder text used to live in winget-install-matrix.yml.
        var workflows = Directory.GetFiles(Path.Combine(RepoRoot(), ".github", "workflows"), "*.yml");
        Assert.NotEmpty(workflows);
        foreach (var path in workflows)
            Assert.DoesNotContain("public partial class", File.ReadAllText(path), StringComparison.Ordinal);
    }
}
