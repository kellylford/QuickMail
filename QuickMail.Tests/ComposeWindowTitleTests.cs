using System.Collections.Generic;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Compose window title derivation, deferred from PR #38 (issue #40).
///
/// The title is "{subject or kind} - {mode} - QuickMail": the subject leads so the taskbar and
/// Alt+Tab identify the message, and the compose mode follows so the editing format is always
/// visible. Both halves are load-bearing for a screen reader user, who hears the window title on
/// every window switch and has no other persistent indication of which format they are typing in.
///
/// Note for anyone comparing against issue #40: that issue predicted the title would read
/// "Untitled" when the subject is blank. It does not — it falls back to the *kind* label
/// ("New Message", "Reply", "Forward", …), which is strictly more informative. These tests pin
/// the shipped behaviour. ("Untitled" is the blank-subject fallback for message TAB titles, a
/// different surface; see TabSessionModelTests.)
/// </summary>
public class ComposeWindowTitleTests
{
    private static ComposeViewModel MakeVm() => new(
        new StubSmtpService(),
        new StubAccountService(),
        new StubCredentialService(),
        new StubImapMailService(),
        new FakeLocalDraftService(),
        new StubTemplateService());

    private static ComposeViewModel MakeVm(ComposeKind kind, string subject = "")
    {
        var vm = MakeVm();
        vm.Seed(new ComposeModel { Kind = kind, Subject = subject });
        return vm;
    }

    public static IEnumerable<object[]> KindLabels() =>
    [
        [ComposeKind.NewMessage,   "New Message"],
        [ComposeKind.Reply,        "Reply"],
        [ComposeKind.ReplyAll,     "Reply All"],
        [ComposeKind.Forward,      "Forward"],
        [ComposeKind.EditDraft,    "Draft"],
        [ComposeKind.NewDraft,     "Draft"],
        [ComposeKind.EditTemplate, "Edit Template"],
    ];

    // ── Subject leads when present ───────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(KindLabels))]
    public void WithASubject_TheSubjectLeadsRegardlessOfKind(ComposeKind kind, string _)
    {
        var vm = MakeVm(kind, "Quarterly review");

        Assert.Equal("Quarterly review - Plain Text - QuickMail", vm.WindowTitle);
    }

    [Fact]
    public void SubjectIsTrimmed()
    {
        var vm = MakeVm(ComposeKind.NewMessage, "   spaced out   ");

        Assert.Equal("spaced out - Plain Text - QuickMail", vm.WindowTitle);
    }

    // ── Kind label leads when the subject is blank ───────────────────────────────

    [Theory]
    [MemberData(nameof(KindLabels))]
    public void WithNoSubject_TheKindLabelLeads(ComposeKind kind, string expectedLabel)
    {
        var vm = MakeVm(kind);

        Assert.Equal($"{expectedLabel} - Plain Text - QuickMail", vm.WindowTitle);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void WhitespaceOnlySubject_CountsAsBlank(string subject)
    {
        var vm = MakeVm(ComposeKind.Reply, subject);

        Assert.Equal("Reply - Plain Text - QuickMail", vm.WindowTitle);
    }

    [Fact]
    public void DefaultKindIsNewMessage()
    {
        var vm = MakeVm(); // never seeded

        Assert.Equal(ComposeKind.NewMessage, vm.ComposeKind);
        Assert.Equal("New Message - Plain Text - QuickMail", vm.WindowTitle);
    }

    // ── Mode segment ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ComposeMode.PlainText, "Plain Text")]
    [InlineData(ComposeMode.Html,      "HTML")]
    [InlineData(ComposeMode.Markdown,  "Markdown")]
    public void ModeSegmentReflectsTheCurrentComposeMode(ComposeMode mode, string expected)
    {
        var vm = MakeVm(ComposeKind.NewMessage, "Subject");
        vm.CurrentMode = mode;

        Assert.Equal($"Subject - {expected} - QuickMail", vm.WindowTitle);
    }

    // ── Change notification ──────────────────────────────────────────────────────

    [Fact]
    public void ChangingTheSubjectRaisesPropertyChangedForWindowTitle()
    {
        // Without this notification the taskbar and Alt+Tab keep the stale title, and a screen
        // reader user switching back hears the wrong message identified.
        var vm = MakeVm();
        var raised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ComposeViewModel.WindowTitle)) raised = true;
        };

        vm.Subject = "New subject";

        Assert.True(raised);
        Assert.Equal("New subject - Plain Text - QuickMail", vm.WindowTitle);
    }

    [Fact]
    public void ChangingTheModeRaisesPropertyChangedForWindowTitle()
    {
        var vm = MakeVm();
        var raised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ComposeViewModel.WindowTitle)) raised = true;
        };

        vm.CurrentMode = ComposeMode.Html;

        Assert.True(raised);
    }

    [Fact]
    public void SeedingRaisesPropertyChangedForWindowTitle()
    {
        var vm = MakeVm();
        var raised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ComposeViewModel.WindowTitle)) raised = true;
        };

        vm.Seed(new ComposeModel { Kind = ComposeKind.Forward });

        Assert.True(raised);
        Assert.Equal("Forward - Plain Text - QuickMail", vm.WindowTitle);
    }
}
