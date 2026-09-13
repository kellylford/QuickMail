using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation.Peers;
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
/// </summary>
public class ControllingTextBox : TextBox
{
    /// <summary>
    /// The control this box drives. Set it before the automation peer is first asked for its
    /// controlled peers — in practice, during the owning window's construction or Loaded.
    /// </summary>
    public UIElement? Controls { get; set; }

    protected override AutomationPeer OnCreateAutomationPeer() => new ControllingTextBoxPeer(this);

    private sealed class ControllingTextBoxPeer : TextBoxAutomationPeer
    {
        private readonly ControllingTextBox _owner;

        public ControllingTextBoxPeer(ControllingTextBox owner) : base(owner) => _owner = owner;

        protected override List<AutomationPeer>? GetControlledPeersCore()
        {
            if (_owner.Controls is null) return null;

            var peer = UIElementAutomationPeer.FromElement(_owner.Controls)
                       ?? UIElementAutomationPeer.CreatePeerForElement(_owner.Controls);

            return peer is null ? null : [peer];
        }
    }
}
