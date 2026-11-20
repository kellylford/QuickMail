using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QuickMail.Helpers;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The Applications key must reach Windows (issue #631).
///
/// <para>
/// The attachment lists open their own ContextMenu from PreviewKeyDown so the window-level
/// fallback cannot answer with the message menu instead. That is right for Shift+F10, whose
/// WM_CONTEXTMENU is generated while DefWindowProc processes the key <em>down</em> — marking the
/// key handled suppresses it, and the menu opened here is the only one. It is wrong for the
/// Applications key, whose WM_CONTEXTMENU is generated on the key <em>up</em>: handling the key
/// down suppresses nothing, the key up still produces a context-menu request, and it arrives at
/// the popup that is by then holding focus and tears the menu straight back down. That is the
/// reported symptom — the menu is announced and then immediately gone, so Shift+F10 is the only
/// way to reach it.
/// </para>
///
/// <para>
/// Nothing about this is visible from reading the handler, which treats the two gestures as
/// interchangeable, so the guard is worth having: the fix is a key that is deliberately
/// <em>not</em> handled, and the obvious "improvement" is to add it back.
/// </para>
/// </summary>
// Loads a real MainWindow, so it belongs in the collection that serializes window-loading tests:
// two of them constructing a MainWindow at once race inside XAML loading (#590).
[Collection("WpfTests")]
public class ContextMenuKeyTests
{
    [Fact]
    public void ShiftF10IsOpenedByTheControl()
        => Assert.True(ContextMenuKeys.OpensMenuOnKeyDown(Key.F10, ModifierKeys.Shift));

    [Theory]
    // The whole point: Windows raises WM_CONTEXTMENU for Apps on key up, so the key down is left
    // alone. Shift+Apps is how some keyboards report the key and must be left alone too.
    [InlineData(Key.Apps, ModifierKeys.None)]
    [InlineData(Key.Apps, ModifierKeys.Shift)]
    // F10 on its own opens the menu bar; with Ctrl or Alt it is not the context-menu gesture.
    [InlineData(Key.F10, ModifierKeys.None)]
    [InlineData(Key.F10, ModifierKeys.Control)]
    [InlineData(Key.F10, ModifierKeys.Alt)]
    [InlineData(Key.Enter, ModifierKeys.None)]
    public void EveryOtherKeyIsLeftToWindows(Key key, ModifierKeys modifiers)
        => Assert.False(ContextMenuKeys.OpensMenuOnKeyDown(key, modifiers));

    /// <summary>
    /// End to end over the real reading-pane attachment list — the surface the bug was reported
    /// against. Pressing the Applications key there must leave the event unhandled, so the key up
    /// can produce the WM_CONTEXTMENU that opens the list's own menu through the platform path.
    /// </summary>
    [StaFact]
    public void AppsKeyOnTheAttachmentListIsNotSwallowed()
    {
        WpfTestHost.EnsureStyles("AccessibleStyles", "ThemedControls");

        var imap     = new StubImapMailService();
        var accounts = new StubAccountService();
        var creds    = new StubCredentialService();
        var store    = new StubLocalStoreService();
        var config   = new StubConfigService();
        var registry = new StubCommandRegistry();

        var vm = new MainViewModel(imap, accounts, creds, store, new StubOAuthService(),
            new StubSyncService(), config, registry, new StubViewService(), new StubRuleService(),
            new StubSmtpService());

        var window = new MainWindow(vm, new StubSmtpService(), accounts, creds, imap,
            new StubOAuthService(), registry, new StubContactService(), config, store,
            new StubViewService(), new StubRuleService(), new StubTemplateService(),
            new StubFeatureGate());
        try
        {
            var list = window.FindName("ReadingPaneAttachmentList") as ListBox;
            Assert.NotNull(list);
            Assert.NotNull(list!.ContextMenu);

            var args = new KeyEventArgs(Keyboard.PrimaryDevice, new StubPresentationSource(), 0, Key.Apps)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            list.RaiseEvent(args);

            Assert.False(args.Handled);
            Assert.False(list.ContextMenu!.IsOpen);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A never-shown window has no real PresentationSource; KeyEventArgs requires one.</summary>
    private sealed class StubPresentationSource : PresentationSource
    {
        private Visual _root = new System.Windows.Shapes.Rectangle();
        public override Visual RootVisual { get => _root; set => _root = value; }
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
