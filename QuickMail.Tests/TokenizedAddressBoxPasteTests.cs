// Regression tests for the address-field paste crash.
//
// Pasting text that contains a comma or semicolon auto-commits the input into
// chips. When the pasted text parsed to no routable address but still held a
// delimiter (e.g. a bare "Last, First" name), CommitCurrentInput wrote the raw
// text — comma and all — back into the TextBox. That assignment re-raised
// TextChanged, which auto-committed again, wrote it back again, and recursed
// without bound until the stack overflowed and the process crashed. The crash
// surfaced inside the screen reader's in-process accessibility event-cache
// module (its hook rides every UIA event on the stack), but the runaway
// recursion was ours.
//
// A real StackOverflowException cannot be caught in .NET — it tears down the
// test host. So the strongest signal here is simply that these tests RUN TO
// COMPLETION: if the re-entrancy guard regressed, the paste below would crash
// the entire test run rather than fail an assertion.

using System.Linq;
using System.Windows;
using System.Windows.Threading;
using QuickMail.Controls;
using Xunit;

namespace QuickMail.Tests;

[Collection("WpfTests")]
public class TokenizedAddressBoxPasteTests
{
    [StaFact]
    public void Paste_CommaNameWithoutAddress_DoesNotRecurse()
    {
        EnsureApplication();
        var box = new TokenizedAddressBox();

        // Simulate pasting a bare "Last, First" name (contains a comma, no '@').
        // Before the fix this recursed until the stack overflowed.
        box.InputBox.Text = "Ford, Kelly";
        DoEvents();

        // No routable address, so no chip is created and the unrecognised text is
        // handed back to the input box for the user to correct.
        Assert.Empty(box.GetChips());
        Assert.Equal("Ford, Kelly", box.InputBox.Text);
    }

    [StaFact]
    public void Paste_SingleCommaSeparator_DoesNotRecurse()
    {
        EnsureApplication();
        var box = new TokenizedAddressBox();

        box.InputBox.Text = "a,b";
        DoEvents();

        Assert.Empty(box.GetChips());
        Assert.Equal("a,b", box.InputBox.Text);
    }

    [StaFact]
    public void Paste_CommaSeparatedValidAddresses_StillCommitsChips()
    {
        // Guards against the fix over-reaching: legitimate comma-delimited
        // addresses must still auto-commit into chips.
        EnsureApplication();
        var box = new TokenizedAddressBox();

        box.InputBox.Text = "alice@example.com, bob@example.com";
        DoEvents();

        var chips = box.GetChips();
        Assert.Equal(2, chips.Count);
        Assert.Contains(chips, c => c.EmailAddress == "alice@example.com");
        Assert.Contains(chips, c => c.EmailAddress == "bob@example.com");
        Assert.Equal(string.Empty, box.InputBox.Text);
    }

    private static void EnsureApplication()
    {
        lock (typeof(Application))
        {
            if (Application.Current == null)
                new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }
    }

    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new System.Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
