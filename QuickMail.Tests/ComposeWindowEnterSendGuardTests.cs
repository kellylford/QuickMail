// Regression guard for issue #201: pressing Enter while the From combo has focus sent
// the message. The cause was the Send button's IsDefault="True" — WPF routes an
// unhandled Enter to the default button, so any control that does not consume Enter
// itself (the From combo, the compose-mode combo, the Subject box, the attachment list)
// became a send gesture. A half-written message going out discloses its contents, so
// this must stay fixed.
//
// The default-button path is driven by WPF's input pipeline (AccessKeyManager), which a
// synthetic RaiseEvent cannot reproduce — so the guard here is structural: assert the
// Send button is not a default button, and assert the From combo consumes a plain Enter.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Xunit;

namespace QuickMail.Tests;

[Collection("WpfTests")]
public class ComposeWindowEnterSendGuardTests
{
    private static QuickMail.Views.ComposeWindow NewHeadlessComposeWindow(StubSmtpService smtp)
    {
        var vm = new QuickMail.ViewModels.ComposeViewModel(
            smtp,
            new StubAccountService(),
            new StubCredentialService(),
            new StubImapMailService(),
            new FakeLocalDraftService(),
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
    public void SendButton_IsNotADefaultButton()
    {
        var window = NewHeadlessComposeWindow(new StubSmtpService());
        try
        {
            var send = window.FindName("SendButton") as Button;
            Assert.NotNull(send);
            Assert.False(send!.IsDefault,
                "Send must not be IsDefault — Enter from the From combo, the mode combo, " +
                "the Subject box or the attachment list would send the message (issue #201).");
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void FromCombo_SwallowsPlainEnter_WhenDropDownIsClosed()
    {
        var smtp = new StubSmtpService();
        var window = NewHeadlessComposeWindow(smtp);
        try
        {
            var from = window.FindName("FromCombo") as ComboBox;
            Assert.NotNull(from);
            Assert.False(from!.IsDropDownOpen);

            var args = NewKeyDown(from!, Key.Return);
            from!.RaiseEvent(args);

            Assert.True(args.Handled, "Enter on a closed From combo must be consumed, not routed onward.");
            Assert.Empty(smtp.Sent);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void FromCombo_LeavesEnterAlone_WhenDropDownIsOpen()
    {
        var window = NewHeadlessComposeWindow(new StubSmtpService());
        try
        {
            window.Show();
            var from = window.FindName("FromCombo") as ComboBox;
            Assert.NotNull(from);
            from!.IsDropDownOpen = true;
            Assert.True(from!.IsDropDownOpen, "test premise: the dropdown must actually be open");

            var args = NewKeyDown(from!, Key.Return);
            from!.RaiseEvent(args);

            // The combo itself commits the highlighted item and closes; our guard must
            // not pre-empt that.
            Assert.False(args.Handled);
        }
        finally
        {
            window.Close();
        }
    }

    private static KeyEventArgs NewKeyDown(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target)
                     ?? PresentationSource.FromVisual(Window.GetWindow(target)!);
        return new KeyEventArgs(Keyboard.PrimaryDevice, source ?? new StubPresentationSource(), 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
    }

    /// <summary>
    /// A never-shown window has no real PresentationSource; KeyEventArgs requires one.
    /// </summary>
    private sealed class StubPresentationSource : PresentationSource
    {
        private Visual _root = new System.Windows.Shapes.Rectangle();
        public override Visual RootVisual { get => _root; set => _root = value; }
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
