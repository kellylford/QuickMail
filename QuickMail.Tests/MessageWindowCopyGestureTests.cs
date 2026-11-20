// Regression tests for #978: in a message opened in its own window, Ctrl+C copied nothing.
// The window's Copy Attachment command owns Ctrl+C and is available only while the attachment
// list has focus, and the key handler swallowed a key whenever any command owned it, so the copy
// never reached the header fields or the message body.

using System.Reflection;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

public class MessageWindowCopyGestureTests
{
    private static CommandRegistry RegistryOf(MessageWindow win) =>
        (CommandRegistry)typeof(MessageWindow)
            .GetField("_localRegistry", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(win)!;

    [StaFact]
    public void CtrlC_outside_the_attachment_list_is_left_for_the_text_to_copy()
    {
        var win = new MessageWindow(new MessageWindowViewModel(), new StubImapMailService(), new StubLocalStoreService());
        try
        {
            var command = RegistryOf(win).FindByGesture(Key.C, ModifierKeys.Control);
            Assert.NotNull(command);
            Assert.Equal("message.copyAttachment", command.Id);
            Assert.False(command.IsAvailable!.Invoke());   // the attachment list does not have focus

            Assert.False(MessageWindow.DispatchRegistryGesture(command, Key.C, ModifierKeys.Control));
        }
        finally
        {
            win.Close();   // constructed windows join Application.Current.Windows (#252)
        }
    }

    [StaFact]
    public void An_unavailable_command_on_another_key_is_still_swallowed()
    {
        // Alt+Left on the first message must not fall through to the page.
        var ran = 0;
        var command = new CommandDefinition(id: "test.prev", category: "View", title: "Previous",
            execute: () => ran++, isAvailable: () => false);

        Assert.True(MessageWindow.DispatchRegistryGesture(command, Key.Left, ModifierKeys.Alt));
        Assert.Equal(0, ran);
    }

    [StaFact]
    public void An_available_command_runs_and_is_handled()
    {
        var ran = 0;
        var command = new CommandDefinition(id: "test.copy", category: "Mail", title: "Copy",
            execute: () => ran++, isAvailable: () => true);

        Assert.True(MessageWindow.DispatchRegistryGesture(command, Key.C, ModifierKeys.Control));
        Assert.Equal(1, ran);
    }

    [StaFact]
    public void An_unavailable_command_on_a_text_gesture_does_not_run()
    {
        var ran = 0;
        var command = new CommandDefinition(id: "test.copy", category: "Mail", title: "Copy",
            execute: () => ran++, isAvailable: () => false);

        Assert.False(MessageWindow.DispatchRegistryGesture(command, Key.Insert, ModifierKeys.Control));
        Assert.Equal(0, ran);
    }
}
