using QuickMail.Models;

namespace QuickMail.ViewModels;

/// <summary>
/// Permanent sentinel tab representing the message list in Tab mode.
/// Always first in the tab strip; cannot be closed by the user.
/// When active, the reading pane is hidden and the message list is shown.
/// </summary>
public sealed class MessageListTabViewModel : TabSessionViewModel
{
    public MessageListTabViewModel()
        : base(new TabSessionModel
        {
            Kind     = TabKind.MessageList,
            Title    = "Messages",
            Tooltip  = "Message list",
            CanClose = false,
        })
    {
        CanClose = false;
    }

    public override bool CanCloseNow() => false;
}
