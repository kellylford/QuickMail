using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// #1050: a reply to a message that has HTML opens in HTML, so the original is quoted as a
/// real quote block instead of lines starting "&gt; " that a screen reader reads out on every
/// line. Replies to plain-text messages, and every other kind of compose, are unchanged.
/// </summary>
[Collection("WpfTests")]
public class ReplyComposeModeTests
{
    private static MailMessageDetail Detail(string plain, string html) => new()
    {
        MessageId     = "1",
        AccountId     = Guid.NewGuid(),
        FolderName    = "Inbox",
        From          = "alice@example.com",
        To            = "bob@example.com",
        Subject       = "Lunch",
        PlainTextBody = plain,
        HtmlBody      = html,
        Date          = DateTimeOffset.UtcNow,
    };

    private static ComposeViewModel NewVm() => new(
        new StubSmtpService(), new StubAccountService(), new StubCredentialService(),
        new StubImapMailService(), new StubTemplateService());

    // ── The decision itself ──────────────────────────────────────────────────

    [Theory]
    // Replies with quoted HTML open in HTML whatever the default is, while the setting is on.
    [InlineData(ComposeKind.Reply,    ComposeMode.PlainText, true,  ComposeMode.PlainText, true,  ComposeMode.Html)]
    [InlineData(ComposeKind.ReplyAll, ComposeMode.PlainText, true,  ComposeMode.PlainText, true,  ComposeMode.Html)]
    [InlineData(ComposeKind.Reply,    ComposeMode.PlainText, true,  ComposeMode.Markdown,  true,  ComposeMode.Html)]
    // Setting off: the default decides, as before.
    [InlineData(ComposeKind.Reply,    ComposeMode.PlainText, true,  ComposeMode.PlainText, false, ComposeMode.PlainText)]
    [InlineData(ComposeKind.Reply,    ComposeMode.PlainText, true,  ComposeMode.Markdown,  false, ComposeMode.Markdown)]
    // A plain-text original has no quoted HTML: the default decides.
    [InlineData(ComposeKind.Reply,    ComposeMode.PlainText, false, ComposeMode.PlainText, true,  ComposeMode.PlainText)]
    [InlineData(ComposeKind.ReplyAll, ComposeMode.PlainText, false, ComposeMode.Html,      true,  ComposeMode.Html)]
    // Other kinds are untouched by the setting.
    [InlineData(ComposeKind.Forward,  ComposeMode.Html,      true,  ComposeMode.PlainText, false, ComposeMode.Html)]
    [InlineData(ComposeKind.Forward,  ComposeMode.PlainText, false, ComposeMode.Markdown,  true,  ComposeMode.Markdown)]
    [InlineData(ComposeKind.NewMessage,ComposeMode.PlainText, false, ComposeMode.PlainText, true,  ComposeMode.PlainText)]
    [InlineData(ComposeKind.EditDraft, ComposeMode.Markdown, false, ComposeMode.Html,      true,  ComposeMode.Markdown)]
    [InlineData(ComposeKind.NewDraft, ComposeMode.PlainText, true,  ComposeMode.Html,      true,  ComposeMode.PlainText)]
    [InlineData(ComposeKind.EditTemplate, ComposeMode.PlainText, true, ComposeMode.Html,   true,  ComposeMode.PlainText)]
    public void ResolveInitialMode(ComposeKind kind, ComposeMode seeded, bool hasQuotedHtml,
        ComposeMode defaultMode, bool replyInHtml, ComposeMode expected)
    {
        Assert.Equal(expected,
            ComposeViewModel.ResolveInitialMode(kind, seeded, hasQuotedHtml, defaultMode, replyInHtml));
    }

    // ── Through a real reply ─────────────────────────────────────────────────

    [Fact]
    public void ReplyToHtmlMessage_OpensInHtml()
    {
        var detail = Detail("Are you free?", "<p>Are you free?</p>");
        var vm = NewVm();
        vm.Seed(ComposeViewModel.CreateReply(detail, detail.AccountId));

        Assert.Equal(ComposeMode.Html, vm.InitialMode(ComposeMode.PlainText, replyInHtmlToHtmlMessages: true));
    }

    [Fact]
    public void ReplyAllToHtmlOnlyMessage_OpensInHtml()
    {
        var detail = Detail("", "<p>Are you free?</p>");
        var vm = NewVm();
        vm.Seed(ComposeViewModel.CreateReplyAll(detail, detail.AccountId));

        Assert.Equal(ComposeMode.Html, vm.InitialMode(ComposeMode.PlainText, replyInHtmlToHtmlMessages: true));
    }

    [Fact]
    public void ReplyToPlainTextMessage_KeepsTheDefault()
    {
        var detail = Detail("Are you free?", "");
        var vm = NewVm();
        vm.Seed(ComposeViewModel.CreateReply(detail, detail.AccountId));

        Assert.Equal(ComposeMode.PlainText, vm.InitialMode(ComposeMode.PlainText, replyInHtmlToHtmlMessages: true));
    }

    [Fact]
    public void ReplyToHtmlMessage_SettingOff_KeepsTheDefault()
    {
        var detail = Detail("Are you free?", "<p>Are you free?</p>");
        var vm = NewVm();
        vm.Seed(ComposeViewModel.CreateReply(detail, detail.AccountId));

        Assert.Equal(ComposeMode.PlainText, vm.InitialMode(ComposeMode.PlainText, replyInHtmlToHtmlMessages: false));
    }

    // ── In the compose window ────────────────────────────────────────────────

    private static (ComposeWindow Window, ComposeViewModel Vm) OpenReplyWindow(
        MailMessageDetail detail, bool replyInHtml)
    {
        var config = new StubConfigService();
        config.Load().DefaultComposeMode = ComposeMode.PlainText;
        config.Load().ReplyInHtmlToHtmlMessages = replyInHtml;
        var vm = NewVm();
        vm.Seed(ComposeViewModel.CreateReply(detail, detail.AccountId));
        var window = new ComposeWindow(vm, new StubContactService(), new StubTemplateService(), config)
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, ConfirmSaveOnClose = null,
        };
        window.Show();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        return (window, vm);
    }

    [StaFact]
    public void ReplyWindow_HtmlOriginal_QuotesItAsAQuoteBlockWithNoGreaterThanSigns()
    {
        var detail = Detail("First line\nSecond line", "<p>First line</p><p>Second line</p>");
        var (window, vm) = OpenReplyWindow(detail, replyInHtml: true);
        try
        {
            Assert.Equal(ComposeMode.Html, vm.CurrentMode);
            var editor = window.FindName("RichBodyBox") as RichTextBox;
            Assert.NotNull(editor);

            var quote = editor!.Document.Blocks.OfType<Section>()
                .SingleOrDefault(s => Equals(s.Tag, RichTextDocumentConverter.TagBlockquote));
            Assert.NotNull(quote);
            var quoted = new TextRange(quote!.ContentStart, quote.ContentEnd).Text;
            Assert.Contains("First line", quoted);
            Assert.Contains("Second line", quoted);

            var all = new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text;
            Assert.DoesNotContain(">", all);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ReplyWindow_HtmlOriginal_SettingOff_StaysPlainTextWithQuoteMarks()
    {
        var detail = Detail("First line", "<p>First line</p>");
        var (window, vm) = OpenReplyWindow(detail, replyInHtml: false);
        try
        {
            Assert.Equal(ComposeMode.PlainText, vm.CurrentMode);
            Assert.Contains("> First line", vm.Body);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ReplyWindow_PlainTextOriginal_StaysPlainText()
    {
        var detail = Detail("First line", "");
        var (window, vm) = OpenReplyWindow(detail, replyInHtml: true);
        try
        {
            Assert.Equal(ComposeMode.PlainText, vm.CurrentMode);
            Assert.Contains("> First line", vm.Body);
        }
        finally { window.Close(); }
    }
}
