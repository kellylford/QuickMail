using System;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for ComposeViewModel mode switching: conversions between Plain Text,
/// Markdown, and HTML modes, the lose-formatting confirmation, and the
/// Mode/HtmlBody values handed to MimeMessageBuilder.
/// </summary>
public class ComposeViewModelModeTests
{
    private static ComposeViewModel MakeVm() => new(
        new StubSmtpService(),
        new StubAccountService(),
        new StubCredentialService(),
        new StubImapMailService(),
        new StubTemplateService());

    [Fact]
    public void SetMode_SameMode_ReturnsFalse()
    {
        var vm = MakeVm();
        Assert.False(vm.SetMode(ComposeMode.PlainText));
    }

    [Fact]
    public void SetMode_PlainToMarkdown_BodyPassesThroughUnchanged()
    {
        var vm = MakeVm();
        vm.Body = "hello world";
        Assert.True(vm.SetMode(ComposeMode.Markdown));
        Assert.Equal(ComposeMode.Markdown, vm.CurrentMode);
        Assert.Equal("hello world", vm.Body);
    }

    [Fact]
    public void SetMode_PlainToHtml_RaisesLoadWithEscapedHtml()
    {
        var vm = MakeVm();
        vm.Body = "a < b";
        string? loaded = null;
        vm.LoadHtmlIntoEditorRequested += html => loaded = html;

        Assert.True(vm.SetMode(ComposeMode.Html));
        Assert.NotNull(loaded);
        Assert.Contains("&lt;", loaded);
        Assert.DoesNotContain("a < b", loaded);
    }

    [Fact]
    public void SetMode_MarkdownToHtml_RaisesLoadWithRenderedHtml()
    {
        var vm = MakeVm();
        vm.SetMode(ComposeMode.Markdown);
        vm.Body = "**bold**";
        string? loaded = null;
        vm.LoadHtmlIntoEditorRequested += html => loaded = html;

        Assert.True(vm.SetMode(ComposeMode.Html));
        Assert.Contains("<strong>bold</strong>", loaded);
    }

    [Fact]
    public void SetMode_MarkdownToPlain_StripsSyntax()
    {
        var vm = MakeVm();
        vm.SetMode(ComposeMode.Markdown);
        vm.Body = "# Title\n\n**bold** text";

        Assert.True(vm.SetMode(ComposeMode.PlainText));
        Assert.DoesNotContain("**", vm.Body);
        Assert.DoesNotContain("#", vm.Body);
        Assert.Contains("Title", vm.Body);
        Assert.Contains("bold text", vm.Body);
    }

    [Fact]
    public void SetMode_MarkdownToPlain_AsksConfirmation_WhenFormattingPresent()
    {
        var vm = MakeVm();
        vm.SetMode(ComposeMode.Markdown);
        vm.Body = "**bold**";
        bool asked = false;
        vm.ConfirmationRequested = (_, _) => { asked = true; return true; };

        vm.SetMode(ComposeMode.PlainText);
        Assert.True(asked);
    }

    [Fact]
    public void SetMode_MarkdownToPlain_UserDeclines_StaysInMarkdown()
    {
        var vm = MakeVm();
        vm.SetMode(ComposeMode.Markdown);
        vm.Body = "**bold**";
        vm.ConfirmationRequested = (_, _) => false;

        Assert.False(vm.SetMode(ComposeMode.PlainText));
        Assert.Equal(ComposeMode.Markdown, vm.CurrentMode);
        Assert.Equal("**bold**", vm.Body);
    }

    [Fact]
    public void SetMode_MarkdownToPlain_NoFormatting_NoConfirmation()
    {
        var vm = MakeVm();
        vm.SetMode(ComposeMode.Markdown);
        vm.Body = "just plain words";
        bool asked = false;
        vm.ConfirmationRequested = (_, _) => { asked = true; return true; };

        Assert.True(vm.SetMode(ComposeMode.PlainText));
        Assert.False(asked);
    }

    [Fact]
    public void SetMode_HtmlToMarkdown_UsesProviderMarkdown()
    {
        var vm = MakeVm();
        vm.SetMode(ComposeMode.Html);
        vm.RichBodyProvider = () => new RichBodySnapshot("<p><strong>hi</strong></p>", "**hi**", "hi");

        Assert.True(vm.SetMode(ComposeMode.Markdown));
        Assert.Equal("**hi**", vm.Body);
    }

    [Fact]
    public void SetMode_HtmlToPlain_UsesProviderPlainText()
    {
        var vm = MakeVm();
        vm.SetMode(ComposeMode.Html);
        vm.RichBodyProvider = () => new RichBodySnapshot("<p><strong>hi</strong></p>", "**hi**", "hi");
        vm.ConfirmationRequested = (_, _) => true;

        Assert.True(vm.SetMode(ComposeMode.PlainText));
        Assert.Equal("hi", vm.Body);
    }

    [Fact]
    public void ModeDisplay_TracksCurrentMode()
    {
        var vm = MakeVm();
        Assert.Equal("Mode: Plain Text", vm.ModeDisplay);
        vm.SetMode(ComposeMode.Markdown);
        Assert.Equal("Mode: Markdown", vm.ModeDisplay);
        vm.SetMode(ComposeMode.Html);
        Assert.Equal("Mode: HTML", vm.ModeDisplay);
    }

    // ── BuildComposeModel: what gets handed to MIME building ─────────────────

    [Fact]
    public void BuildComposeModel_PlainTextMode_NoHtmlBody()
    {
        var vm = MakeVm();
        vm.Body = "plain";
        var model = vm.BuildComposeModel(Guid.NewGuid());
        Assert.Equal(ComposeMode.PlainText, model.Mode);
        Assert.Null(model.HtmlBody);
        Assert.Equal("plain", model.Body);
    }

    [Fact]
    public void BuildComposeModel_MarkdownMode_HtmlBodyRendered_PlainPartIsSource()
    {
        var vm = MakeVm();
        vm.SetMode(ComposeMode.Markdown);
        vm.Body = "**bold**";
        var model = vm.BuildComposeModel(Guid.NewGuid());

        Assert.Equal(ComposeMode.Markdown, model.Mode);
        Assert.NotNull(model.HtmlBody);
        Assert.Contains("<strong>bold</strong>", model.HtmlBody);
        Assert.Equal("**bold**", model.Body);   // markdown source reads naturally as text
    }

    [Fact]
    public void BuildComposeModel_MarkdownMode_EmptyBody_StaysPlainOnly()
    {
        var vm = MakeVm();
        vm.SetMode(ComposeMode.Markdown);
        vm.Body = "";
        var model = vm.BuildComposeModel(Guid.NewGuid());
        Assert.Null(model.HtmlBody);
    }

    [Fact]
    public void BuildComposeModel_HtmlMode_UsesSnapshotHtmlAndPlainText()
    {
        var vm = MakeVm();
        vm.SetMode(ComposeMode.Html);
        vm.RichBodyProvider = () => new RichBodySnapshot("<p><em>fancy</em></p>", "*fancy*", "fancy");
        var model = vm.BuildComposeModel(Guid.NewGuid());

        Assert.Equal(ComposeMode.Html, model.Mode);
        Assert.Contains("<em>fancy</em>", model.HtmlBody);
        Assert.Equal("fancy", model.Body);
    }

    [Fact]
    public void BuildComposeModel_HtmlMode_EmptyEditor_StaysPlainOnly()
    {
        var vm = MakeVm();
        vm.SetMode(ComposeMode.Html);
        vm.RichBodyProvider = () => RichBodySnapshot.Empty;
        var model = vm.BuildComposeModel(Guid.NewGuid());
        Assert.Null(model.HtmlBody);
    }

    // ── Window title: subject + mode, shown in the taskbar and Alt+Tab ───────

    [Fact]
    public void WindowTitle_NoSubject_ShowsKindModeAndApp()
    {
        var vm = MakeVm();
        Assert.Equal("New Message - Plain Text - QuickMail", vm.WindowTitle);
    }

    [Fact]
    public void WindowTitle_WithSubject_LeadsWithSubject()
    {
        var vm = MakeVm();
        vm.Subject = "Lunch on Friday";
        Assert.Equal("Lunch on Friday - Plain Text - QuickMail", vm.WindowTitle);
    }

    [Fact]
    public void WindowTitle_ReflectsModeChanges()
    {
        var vm = MakeVm();
        vm.Subject = "Lunch on Friday";

        var titleChanges = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.WindowTitle)) titleChanges++;
        };

        vm.SetMode(ComposeMode.Html);
        Assert.Equal("Lunch on Friday - HTML - QuickMail", vm.WindowTitle);
        Assert.True(titleChanges > 0, "mode change must notify WindowTitle so the taskbar updates");

        vm.SetMode(ComposeMode.Markdown);
        Assert.Equal("Lunch on Friday - Markdown - QuickMail", vm.WindowTitle);
    }
}
