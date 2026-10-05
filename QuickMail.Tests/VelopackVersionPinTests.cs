// The Velopack library the app references and the vpk CLI that packs releases must be the same
// version. vpk warns when the library is older and errors when it is newer ("In a future version this
// may become a fatal error"), and the two pins live in different files that different tools bump:
// Dependabot proposes the NuGet package, while .github/vpk-version is edited by hand. This test is what
// makes "keep them at the same version" (docs/INSTALLER.md) hold — a bump of one alone fails here.

using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace QuickMail.Tests;

public class VelopackVersionPinTests
{
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, ".github", "vpk-version")))
                return dir.FullName;
        throw new InvalidOperationException("Repo source tree not found from " + AppContext.BaseDirectory + ".");
    }

    [Fact]
    public void TheVelopackLibrary_MatchesThePinnedCli()
    {
        var root = RepoRoot();
        var cli = File.ReadAllText(Path.Combine(root, ".github", "vpk-version")).Trim();
        var csproj = File.ReadAllText(Path.Combine(root, "QuickMail", "QuickMail.csproj"));
        var match = Regex.Match(csproj, "<PackageReference\\s+Include=\"Velopack\"\\s+Version=\"([^\"]+)\"");

        Assert.True(match.Success, "QuickMail.csproj has no Velopack PackageReference with a Version attribute.");
        Assert.Equal(cli, match.Groups[1].Value);
    }
}
