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
/// line. The same setting keeps a reply to a plain-text message out of HTML when the default
/// compose mode is HTML. Every other kind of compose is unchanged.
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
    // A plain-text original has no quoted HTML: the reply does not open in HTML. An HTML default
    // becomes Plain Text; Plain Text and Markdown defaults are kept.
    [InlineData(ComposeKind.Reply,    ComposeMode.PlainText, false, ComposeMode.PlainText, true,  ComposeMode.PlainText)]
    [InlineData(ComposeKind.Reply,    ComposeMode.PlainText, false, ComposeMode.Html,      true,  ComposeMode.PlainText)]
    [InlineData(ComposeKind.ReplyAll, ComposeMode.PlainText, false, ComposeMode.Html,      true,  ComposeMode.PlainText)]
    [InlineData(ComposeKind.Reply,    ComposeMode.PlainText, false, ComposeMode.Markdown,  true,  ComposeMode.Markdown)]
    // Setting off, a plain-text original follows the default, HTML included.
    [InlineData(ComposeKind.ReplyAll, ComposeMode.PlainText, false, ComposeMode.Html,      false, ComposeMode.Html)]
    // An HTML original with an HTML default stays HTML either way.
    [InlineData(ComposeKind.Reply,    ComposeMode.PlainText, true,  ComposeMode.Html,      true,  ComposeMode.Html)]
    [InlineData(ComposeKind.Reply,    ComposeMode.PlainText, true,  ComposeMode.Html,      false, ComposeMode.Html)]
    // Forwards and new messages keep an HTML default, setting on: only replies follow the original.
    [InlineData(ComposeKind.Forward,  ComposeMode.PlainText, false, ComposeMode.Html,      true,  ComposeMode.Html)]
    [InlineData(ComposeKind.NewMessage,ComposeMode.PlainText, false, ComposeMode.Html,     true,  ComposeMode.Html)]
    // Other kinds are untouched by the setting.
    [InlineData(ComposeKind.Forward,  ComposeMode.Html,      true,  ComposeMode.PlainText, false, ComposeMode.Html)]
    [InlineData(ComposeKind.Forward,  ComposeMode.PlainText, false, ComposeMode.Markdown,  true,  ComposeMode.Markdown)]
    [InlineData(ComposeKind.NewMessage,ComposeMode.PlainText, false, ComposeMode.PlainText, true,  ComposeMode.PlainText)]
    [InlineData(ComposeKind.EditDraft, ComposeMode.Markdown, false, ComposeMode.Html,      true,  ComposeMode.Markdown)]
    [InlineData(ComposeKind.NewDraft, ComposeMode.PlainText, true,  ComposeMode.Html,      true,  ComposeMode.PlainText)]
    [InlineData(ComposeKind.EditTemplate, ComposeMode.PlainText, true, ComposeMode.Html,   true,  ComposeMode.PlainText)]
    public void ResolveInitialMode(ComposeKind kind, ComposeMode seeded, bool hasQuotedHtml,
        ComposeMode defaultMode, bool replyInOriginalFormat, ComposeMode expected)
    {
        Assert.Equal(expected,
            ComposeViewModel.ResolveInitialMode(kind, seeded, hasQuotedHtml, reopenedFromOutbox: false,
                defaultMode, replyInOriginalFormat));
    }

    // A message reopened from the Outbox comes back in the mode it was written in. A queued reply
    // is still Kind=Reply and still carries HTML, so without this a reply queued in Markdown
    // would come back in HTML.
    [Theory]
    [InlineData(ComposeKind.Reply,      ComposeMode.Markdown,  ComposeMode.PlainText, true)]
    [InlineData(ComposeKind.ReplyAll,   ComposeMode.PlainText, ComposeMode.Html,      true)]
    [InlineData(ComposeKind.Reply,      ComposeMode.Html,      ComposeMode.PlainText, false)]
    [InlineData(ComposeKind.NewMessage, ComposeMode.Markdown,  ComposeMode.PlainText, true)]
    [InlineData(ComposeKind.Forward,    ComposeMode.PlainText, ComposeMode.Html,      true)]
    public void ResolveInitialMode_ReopenedFromOutbox_RestoresTheModeItWasWrittenIn(
        ComposeKind kind, ComposeMode seeded, ComposeMode defaultMode, bool replyInOriginalFormat)
    {
        Assert.Equal(seeded,
            ComposeViewModel.ResolveInitialMode(kind, seeded, hasQuotedHtml: true, reopenedFromOutbox: true,
                defaultMode, replyInOriginalFormat));
    }

    [Fact]
    public void ReplyQueuedInMarkdown_ReopensFromOutboxInMarkdown()
    {
        var vm = NewVm();
        vm.Seed(new ComposeModel
        {
            Kind     = ComposeKind.Reply,
            OutboxId = "queued-1",
            Mode     = ComposeMode.Markdown,
            Body     = "**Yes**\n\n> Are you free?",
            HtmlBody = "<html><body><p><strong>Yes</strong></p><blockquote><p>Are you free?</p></blockquote></body></html>",
        });

        Assert.Equal(ComposeMode.Markdown, vm.InitialMode(ComposeMode.PlainText, replyInOriginalFormat: true));
    }

    // ── Through a real reply ─────────────────────────────────────────────────

    [Fact]
    public void ReplyToHtmlMessage_OpensInHtml()
    {
        var detail = Detail("Are you free?", "<p>Are you free?</p>");
        var vm = NewVm();
        vm.Seed(ComposeViewModel.CreateReply(detail, detail.AccountId));

        Assert.Equal(ComposeMode.Html, vm.InitialMode(ComposeMode.PlainText, replyInOriginalFormat: true));
    }

    [Fact]
    public void ReplyAllToHtmlOnlyMessage_OpensInHtml()
    {
        var detail = Detail("", "<p>Are you free?</p>");
        var vm = NewVm();
        vm.Seed(ComposeViewModel.CreateReplyAll(detail, detail.AccountId));

        Assert.Equal(ComposeMode.Html, vm.InitialMode(ComposeMode.PlainText, replyInOriginalFormat: true));
    }

    [Fact]
    public void ReplyToPlainTextMessage_KeepsTheDefault()
    {
        var detail = Detail("Are you free?", "");
        var vm = NewVm();
        vm.Seed(ComposeViewModel.CreateReply(detail, detail.AccountId));

        Assert.Equal(ComposeMode.PlainText, vm.InitialMode(ComposeMode.PlainText, replyInOriginalFormat: true));
    }

    // The reported case: Default compose mode is HTML, and a reply to a plain-text message opened
    // in HTML. Through a real CreateReply, so it also proves a plain-text original seeds no HTML.
    [Fact]
    public void ReplyToPlainTextMessage_HtmlDefault_OpensInPlainText()
    {
        var detail = Detail("Are you free?", "");
        var vm = NewVm();
        vm.Seed(ComposeViewModel.CreateReply(detail, detail.AccountId));

        Assert.Equal(ComposeMode.PlainText, vm.InitialMode(ComposeMode.Html, replyInOriginalFormat: true));
    }

    [Fact]
    public void ReplyAllToPlainTextMessage_HtmlDefault_SettingOff_OpensInHtml()
    {
        var detail = Detail("Are you free?", "");
        var vm = NewVm();
        vm.Seed(ComposeViewModel.CreateReplyAll(detail, detail.AccountId));

        Assert.Equal(ComposeMode.Html, vm.InitialMode(ComposeMode.Html, replyInOriginalFormat: false));
    }

    [Fact]
    public void ReplyToHtmlMessage_SettingOff_KeepsTheDefault()
    {
        var detail = Detail("Are you free?", "<p>Are you free?</p>");
        var vm = NewVm();
        vm.Seed(ComposeViewModel.CreateReply(detail, detail.AccountId));

        Assert.Equal(ComposeMode.PlainText, vm.InitialMode(ComposeMode.PlainText, replyInOriginalFormat: false));
    }

    // ── In the compose window ────────────────────────────────────────────────

    private sealed class SignedAccountService(AccountModel account) : QuickMail.Services.IAccountService
    {
        public System.Collections.Generic.List<AccountModel> LoadAccounts() => [account];
        public void SaveAccounts(System.Collections.Generic.List<AccountModel> accounts) { }
        public void SetDefaultAccount(Guid accountId) { }
    }

    private static (ComposeWindow Window, ComposeViewModel Vm) OpenReplyWindow(
        MailMessageDetail detail, bool replyInOriginalFormat, string? signature = null,
        ComposeMode defaultMode = ComposeMode.PlainText)
    {
        var config = new StubConfigService();
        config.Load().DefaultComposeMode = defaultMode;
        config.Load().ReplyInOriginalFormat = replyInOriginalFormat;
        var account = new AccountModel { Id = Guid.NewGuid(), IsDefault = true, Signature = signature ?? string.Empty };
        var vm = new ComposeViewModel(
            new StubSmtpService(), new SignedAccountService(account), new StubCredentialService(),
            new StubImapMailService(), new StubTemplateService());
        vm.Seed(ComposeViewModel.CreateReply(detail, account.Id));
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
        var (window, vm) = OpenReplyWindow(detail, replyInOriginalFormat: true);
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

    // The reported case, in a real window: Default compose mode HTML, a plain-text original, the
    // setting on. The reply must open in the plain-text editor, not the rich one.
    [StaFact]
    public void ReplyWindow_PlainOriginal_HtmlDefault_OpensInThePlainTextEditor()
    {
        var detail = Detail("Are you free?", "");
        var (window, vm) = OpenReplyWindow(detail, replyInOriginalFormat: true, defaultMode: ComposeMode.Html);
        try
        {
            Assert.Equal(ComposeMode.PlainText, vm.CurrentMode);
            var plain = window.FindName("BodyBox") as TextBox;
            var rich  = window.FindName("RichBodyBox") as RichTextBox;
            Assert.NotNull(plain);
            Assert.NotNull(rich);
            Assert.Equal(Visibility.Visible, plain!.Visibility);
            Assert.NotEqual(Visibility.Visible, rich!.Visibility);
            Assert.Contains("> Are you free?", vm.Body);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ReplyWindow_PlainOriginal_HtmlDefault_SettingOff_OpensInHtml()
    {
        var detail = Detail("Are you free?", "");
        var (window, vm) = OpenReplyWindow(detail, replyInOriginalFormat: false, defaultMode: ComposeMode.Html);
        try
        {
            Assert.Equal(ComposeMode.Html, vm.CurrentMode);
            var rich = window.FindName("RichBodyBox") as RichTextBox;
            Assert.NotNull(rich);
            Assert.Equal(Visibility.Visible, rich!.Visibility);
        }
        finally { window.Close(); }
    }

    // The caret must start in the empty paragraph above "On ... wrote:", outside the quote, so
    // the user types their reply at the top rather than inside the quoted message.
    [StaFact]
    public void ReplyWindow_HtmlOriginal_CaretStartsInAnEmptyLineAboveTheQuote()
    {
        var detail = Detail("First line", "<p>First line</p>");
        var (window, _) = OpenReplyWindow(detail, replyInOriginalFormat: true);
        try
        {
            var editor = window.FindName("RichBodyBox") as RichTextBox;
            Assert.NotNull(editor);
            var paragraph = editor!.CaretPosition.Paragraph;
            Assert.NotNull(paragraph);
            Assert.Same(editor.Document.Blocks.FirstBlock, paragraph);
            Assert.Equal(string.Empty, new TextRange(paragraph!.ContentStart, paragraph.ContentEnd).Text.Trim());
            Assert.Equal(0, RichTextDocumentConverter.QuoteDepth(paragraph));
        }
        finally { window.Close(); }
    }

    // A signature goes through the seeded-HTML path too (_seededHtmlBody += SignatureHtml), which
    // is now what every reply to an HTML message takes.
    [StaFact]
    public void ReplyWindow_HtmlOriginal_WithSignature_PutsTheSignatureAfterTheQuote()
    {
        var detail = Detail("First line", "<p>First line</p>");
        var (window, vm) = OpenReplyWindow(detail, replyInOriginalFormat: true, signature: "Kelly\nThe Idea Place");
        try
        {
            Assert.Equal(ComposeMode.Html, vm.CurrentMode);
            var editor = window.FindName("RichBodyBox") as RichTextBox;
            Assert.NotNull(editor);

            var blocks = editor!.Document.Blocks.ToList();
            var quoteIndex = blocks.FindIndex(b => b is Section s && Equals(s.Tag, RichTextDocumentConverter.TagBlockquote));
            Assert.True(quoteIndex >= 0, "the reply should hold the original as a quote block");
            var after = string.Concat(blocks.Skip(quoteIndex + 1)
                .Select(b => new TextRange(b.ContentStart, b.ContentEnd).Text));
            Assert.Contains("The Idea Place", after);

            var all = new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text;
            Assert.DoesNotContain(">", all);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ReplyWindow_HtmlOriginal_SettingOff_StaysPlainTextWithQuoteMarks()
    {
        var detail = Detail("First line", "<p>First line</p>");
        var (window, vm) = OpenReplyWindow(detail, replyInOriginalFormat: false);
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
        var (window, vm) = OpenReplyWindow(detail, replyInOriginalFormat: true);
        try
        {
            Assert.Equal(ComposeMode.PlainText, vm.CurrentMode);
            Assert.Contains("> First line", vm.Body);
        }
        finally { window.Close(); }
    }
}
