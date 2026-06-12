// Regression tests for compose-editor screen reader accessibility.
//
// Root cause being guarded: WPF's RichTextBoxAutomationPeer binds its UIA
// TextPattern to the text container of the document present at peer creation
// and never rebinds. If RichTextBox.Document is replaced (as the compose
// window once did when entering HTML mode), every UIA text query — even from
// a freshly created peer — keeps reporting the original, now-detached
// document. Screen readers then read nothing of what is actually on screen.
//
// The fix is to keep one FlowDocument for the editor's lifetime and load
// content into it via RichTextDocumentConverter.LoadInto, which these tests
// verify end-to-end through the real ComposeWindow.

using System;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Documents;
using Xunit;

namespace QuickMail.Tests;

[Collection("WpfTests")]
public class ComposeUiaTextPatternTests
{
    [StaFact]
    public void RichTextBox_TextPattern_TracksInPlaceBlockMutation()
    {
        var rtb = new RichTextBox();
        var window = MakeHiddenWindow(rtb);
        window.Show();
        try
        {
            rtb.Document.Blocks.Clear();
            rtb.Document.Blocks.Add(new Paragraph(new Run("original text")));

            var peer = UIElementAutomationPeer.CreatePeerForElement(rtb);
            var textProvider = peer.GetPattern(PatternInterface.Text) as ITextProvider;
            Assert.NotNull(textProvider);
            Assert.Contains("original text", textProvider!.DocumentRange.GetText(-1));

            // Loading through LoadInto must keep the same text container alive.
            QuickMail.Helpers.RichTextDocumentConverter.LoadInto(
                rtb.Document, "<p>mutated <strong>rich</strong> text</p>");

            var after = textProvider.DocumentRange.GetText(-1);
            Assert.Contains("mutated rich text", after);
            Assert.DoesNotContain("original", after);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void ComposeWindow_AllModes_ExposeBodyTextThroughUia()
    {
        var vm = new QuickMail.ViewModels.ComposeViewModel(
            new StubSmtpService(),
            new StubAccountService(),
            new StubCredentialService(),
            new StubImapMailService(),
            new StubTemplateService());

        var window = new QuickMail.Views.ComposeWindow(
            vm, new StubContactService(), new StubTemplateService(), new StubConfigService())
        {
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            ConfirmSaveOnClose = null, // headless — discard on close, no dialog
        };
        window.Show();
        try
        {
            vm.Body = "plain body text";

            var bodyBox = window.FindName("BodyBox") as TextBox;
            var richBodyBox = window.FindName("RichBodyBox") as RichTextBox;
            Assert.NotNull(bodyBox);
            Assert.NotNull(richBodyBox);

            Assert.Contains("plain body text", UiaText(bodyBox!));

            vm.SetMode(QuickMail.Models.ComposeMode.Markdown);
            vm.Body = "markdown **body** text";
            Assert.Contains("markdown **body** text", UiaText(bodyBox!));

            // Entering HTML mode converts the body into the rich editor. The
            // converted text must be visible to UIA — this is what screen
            // readers read while the user navigates and types.
            vm.SetMode(QuickMail.Models.ComposeMode.Html);
            Assert.Contains("markdown body text", UiaText(richBodyBox!));

            // A second round-trip (HTML → Markdown → HTML) loads the editor
            // again; UIA must still track.
            vm.SetMode(QuickMail.Models.ComposeMode.Markdown);
            vm.SetMode(QuickMail.Models.ComposeMode.Html);
            Assert.Contains("markdown body text", UiaText(richBodyBox!));
        }
        finally
        {
            window.Close();
        }
    }

    private static string UiaText(UIElement element)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(element);
        var textProvider = peer.GetPattern(PatternInterface.Text) as ITextProvider;
        Assert.NotNull(textProvider);
        return textProvider!.DocumentRange.GetText(-1);
    }

    private static Window MakeHiddenWindow(UIElement content) => new()
    {
        WindowStyle = WindowStyle.None,
        ShowInTaskbar = false,
        ShowActivated = false,
        Width = 300,
        Height = 200,
        Content = content,
    };
}
