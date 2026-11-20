using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace QuickMail.Controls;

/// <summary>
/// A <see cref="TextBox"/> that tells UI Automation which control it is driving, via the
/// <c>ControllerFor</c> property (<see cref="AutomationPeer.GetControlledPeersCore"/>).
///
/// <para>This is the autocomplete shape a Windows search field uses: keyboard focus stays in
/// the edit box while the list below it narrows, and the screen reader learns which list to
/// report the active row from. Paired with a <c>SelectionItemPatternOnElementSelected</c>
/// event raised on the selected row, the platform speaks the row's name, its position in the
/// set, and its help text — so nothing has to be announced by hand, and no announcement
/// setting can silence it.</para>
///
/// <para>The alternative, moving keyboard focus onto the row itself, is what the command
/// palette did until 2026-05 and why filtering was removed: every keystroke bounced focus
/// between the box and the list. Focus must not move.</para>
///
/// <para>A list that comes and goes, such as the address suggestions, is set and cleared with
/// <see cref="SetControls"/>, which raises the <c>ControllerFor</c> property change. That change
/// on the focused edit is what Windows' suggestion handling listens for: screen readers use it
/// to report that suggestions appeared or went away (Narrator's "suggestions available" sound,
/// for one). Setting <see cref="Controls"/> directly raises nothing, which is right only for a
/// list that is there for the life of the box, as in the command palette.</para>
///
/// <para>WPF keys implicit styles on the exact type, so the implicit TextBox style does not reach
/// this subclass: a use of it sets <c>Style="{DynamicResource {x:Type TextBox}}"</c> by hand.</para>
/// </summary>
public class ControllingTextBox : TextBox
{
    /// <summary>
    /// The control this box drives. Set it before the automation peer is first asked for its
    /// controlled peers — in practice, during the owning window's construction or Loaded — or
    /// use <see cref="SetControls"/> for one that appears and disappears.
    /// </summary>
    public UIElement? Controls { get; set; }

    /// <summary>
    /// Sets or clears the control this box drives and, if it changed, raises the
    /// <c>ControllerFor</c> property-changed event so a screen reader learns of it.
    /// </summary>
    public void SetControls(UIElement? target)
    {
        var old = Controls;
        if (ReferenceEquals(old, target)) return;
        Controls = target;
        (UIElementAutomationPeer.FromElement(this) as ControllingTextBoxPeer)?.RaiseControllerForChanged(old, target);
    }

    /// <summary>
    /// Raises UIA's layout-invalidated event on the control this box drives: what Windows'
    /// suggestion handling expects when a list with nothing selected changes its contents. WPF's
    /// <see cref="AutomationEvents"/> has no member for it, so it is raised on the raw provider.
    /// </summary>
    public void RaiseControlsLayoutInvalidated() =>
        (UIElementAutomationPeer.FromElement(this) as ControllingTextBoxPeer)?.RaiseLayoutInvalidatedOnControlled();

    protected override AutomationPeer OnCreateAutomationPeer() => new ControllingTextBoxPeer(this);

    private sealed class ControllingTextBoxPeer : TextBoxAutomationPeer
    {
        private readonly ControllingTextBox _owner;

        public ControllingTextBoxPeer(ControllingTextBox owner) : base(owner) => _owner = owner;

        protected override List<AutomationPeer>? GetControlledPeersCore()
        {
            var peer = PeerFor(_owner.Controls);
            return peer is null ? null : [peer];
        }

        /// <summary>
        /// The event carries the providers themselves, as UIA's own value for the property does:
        /// a screen reader may read the new value from the event rather than asking again.
        /// </summary>
        internal void RaiseControllerForChanged(UIElement? oldTarget, UIElement? newTarget)
        {
            RaisePropertyChangedEvent(AutomationElementIdentifiers.ControllerForProperty,
                Providers(oldTarget), Providers(newTarget));
        }

        internal void RaiseLayoutInvalidatedOnControlled()
        {
            if (!AutomationInteropProvider.ClientsAreListening) return;
            var peer = PeerFor(_owner.Controls);
            if (peer is null || ProviderFromPeer(peer) is not { } provider) return;
            AutomationInteropProvider.RaiseAutomationEvent(AutomationElementIdentifiers.LayoutInvalidatedEvent,
                provider, new AutomationEventArgs(AutomationElementIdentifiers.LayoutInvalidatedEvent));
        }

        private IRawElementProviderSimple[] Providers(UIElement? target)
        {
            var peer = PeerFor(target);
            var provider = peer is null ? null : ProviderFromPeer(peer);
            return provider is null ? [] : [provider];
        }

        private static AutomationPeer? PeerFor(UIElement? target) => target is null
            ? null
            : UIElementAutomationPeer.FromElement(target) ?? UIElementAutomationPeer.CreatePeerForElement(target);
    }
}
