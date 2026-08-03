using System;
using System.IO;
using System.Linq;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Issue #483: a link opened from a message window landed in an in-app WebView2 popup
/// instead of the user's default browser — no browser cookies, passkeys, or password
/// manager. The reading pane was fine; the message window was not.
///
/// The cause is that WebView2 splits link activation across two events. An ordinary link
/// raises <c>NavigationStarting</c>; a link carrying <c>target="_blank"</c>, or one
/// activated with Ctrl/Shift/middle-click, raises <c>NewWindowRequested</c> INSTEAD, and
/// the parent WebView never sees a NavigationStarting for it. Handling only the first
/// event looks correct in every hand-written test message and fails on real newsletter
/// mail, which is why this shipped.
///
/// These are source-text guards rather than behaviour tests because exercising the real
/// events requires an initialised WebView2 runtime and a live network navigation. They
/// discover the hosts from the XAML, so a new WebView2-hosting window is covered the day
/// it is added — no test-side registration list to forget.
/// </summary>
public class ExternalLinkWiringTests
{
    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "QuickMail", "Views")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!;
    }

    /// <summary>
    /// Every Views/*.xaml that instantiates a WebView2, paired with its code-behind.
    /// Matches the element tag, not the bare word: several views mention WebView2 only in a
    /// comment or carry a leftover xmlns for it without hosting one.
    /// </summary>
    public static TheoryData<string> WebViewHosts()
    {
        var viewsDir = Path.Combine(RepoRoot().FullName, "QuickMail", "Views");
        var element  = new System.Text.RegularExpressions.Regex(@"<[A-Za-z0-9_.]*:?WebView2\b");
        var data     = new TheoryData<string>();
        foreach (var xaml in Directory.EnumerateFiles(viewsDir, "*.xaml"))
        {
            if (!element.IsMatch(File.ReadAllText(xaml))) continue;
            data.Add(Path.GetFileName(xaml));
        }
        return data;
    }

    [Fact]
    public void WebViewHosts_AreDiscovered()
    {
        // Guards the discovery itself: if the XAML scan silently found nothing, every
        // [Theory] case below would vacuously pass.
        Assert.NotEmpty(WebViewHosts());
    }

    [Theory]
    [MemberData(nameof(WebViewHosts))]
    public void EveryWebViewHost_HandlesNewWindowRequested(string xamlFileName)
    {
        var codeBehind = Path.Combine(RepoRoot().FullName, "QuickMail", "Views", xamlFileName + ".cs");
        Assert.True(File.Exists(codeBehind), $"missing code-behind for {xamlFileName}");
        var source = File.ReadAllText(codeBehind);

        Assert.True(
            source.Contains("NewWindowRequested", StringComparison.Ordinal),
            $"{xamlFileName}.cs hosts a WebView2 but never handles NewWindowRequested. " +
            "Without it, target=\"_blank\" links and Ctrl/Shift/middle-clicks open in an " +
            "in-app WebView2 popup instead of the user's default browser (issue #483).");
    }

    [Theory]
    [MemberData(nameof(WebViewHosts))]
    public void EveryWebViewHost_RoutesExternalLinksThroughThePolicy(string xamlFileName)
    {
        var codeBehind = Path.Combine(RepoRoot().FullName, "QuickMail", "Views", xamlFileName + ".cs");
        var source = File.ReadAllText(codeBehind);

        // Message content is untrusted, so the URI must go through ExternalUriPolicy's
        // scheme allow-list rather than straight to ShellExecute. MessageWindow reaches it
        // through a local OpenExternal wrapper.
        Assert.True(
            source.Contains("ExternalUriPolicy", StringComparison.Ordinal) ||
            source.Contains("OpenExternal", StringComparison.Ordinal),
            $"{xamlFileName}.cs must hand external URIs to ExternalUriPolicy, not to Process.Start directly.");
    }
}
