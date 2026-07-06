// Regression guard for issue #181: spell-check must not be enabled during
// ComposeWindow construction (InitializeComponent) — that ran WPF's speller COM
// activation synchronously on the STA thread and could hang the app. Instead it is
// enabled from code, deferred to Background dispatcher priority. These tests verify
// the deferral actually lands (a future edit dropping an editor from the enable set,
// or the deferral silently never firing, would otherwise pass unnoticed and silently
// disable spell-check on the compose editors).

using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Xunit;

namespace QuickMail.Tests;

[Collection("WpfTests")]
public class ComposeWindowSpellCheckTests
{
    private static QuickMail.Views.ComposeWindow NewHeadlessComposeWindow()
    {
        var vm = new QuickMail.ViewModels.ComposeViewModel(
            new StubSmtpService(),
            new StubAccountService(),
            new StubCredentialService(),
            new StubImapMailService(),
            new StubTemplateService());

        return new QuickMail.Views.ComposeWindow(
            vm, new StubContactService(), new StubTemplateService(), new StubConfigService())
        {
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            ConfirmSaveOnClose = null, // headless — discard on close, no dialog
        };
    }

    [StaFact]
    public void SpellCheck_IsNotEnabledDuringConstruction_ButEnabledAfterBackgroundIdle()
    {
        var window = NewHeadlessComposeWindow();

        var subject = window.FindName("SubjectBox") as TextBox;
        var body = window.FindName("BodyBox") as TextBox;
        var richBody = window.FindName("RichBodyBox") as RichTextBox;
        Assert.NotNull(subject);
        Assert.NotNull(body);
        Assert.NotNull(richBody);

        // Before the window is shown and the dispatcher idles, the speller must NOT
        // have been turned on synchronously (that is the #181 hang path).
        Assert.False(SpellCheck.GetIsEnabled(subject!));
        Assert.False(SpellCheck.GetIsEnabled(body!));
        Assert.False(SpellCheck.GetIsEnabled(richBody!));

        window.Show();
        try
        {
            // Drain the dispatcher queue down to Background priority — this runs the
            // deferred EnableSpellCheckDeferred callback queued during construction.
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

            Assert.True(SpellCheck.GetIsEnabled(subject!));
            Assert.True(SpellCheck.GetIsEnabled(body!));
            Assert.True(SpellCheck.GetIsEnabled(richBody!));
        }
        finally
        {
            window.Close();
        }
    }
}
