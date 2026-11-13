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

    [StaFact]
    public void Paste_SemicolonSeparatedList_MakesOneChipPerAddress()
    {
        // #979: MimeKit cannot read a semicolon list, and the fallback made one chip of the whole string.
        EnsureApplication();
        var box = new TokenizedAddressBox();

        box.InputBox.Text = "Smith, John <john@example.com>; Doe, Jane <jane@example.com>";
        DoEvents();

        var chips = box.GetChips();
        Assert.Equal(new[] { "john@example.com", "jane@example.com" }, chips.Select(c => c.EmailAddress));
        Assert.Equal("Smith, John", chips[0].DisplayName);
        Assert.Equal(string.Empty, box.InputBox.Text);
    }

    [StaFact]
    public void CopiedChips_PastedIntoAnotherBox_RoundTrip()
    {
        // #979: copying several chips writes them joined with "; " — that text must paste back as the same chips.
        EnsureApplication();
        var source = new TokenizedAddressBox();
        source.InputBox.Text = "ann@example.com, Ben Baker <ben@example.com>, \"Doe; Jane\" <jane@example.com>, \"Smith, John\" <john@example.com>";
        DoEvents();
        Assert.Equal(4, source.GetChips().Count);
        var copied = string.Join("; ", source.GetChips().Select(c => c.Serialize()));

        var target = new TokenizedAddressBox();
        target.InputBox.Text = copied;
        DoEvents();

        Assert.Equal(source.GetChips().Select(c => c.FullAddress), target.GetChips().Select(c => c.FullAddress));
    }

    [StaFact]
    public void BoundAddressText_WithSemicolons_LoadsOneChipPerAddress()
    {
        // The box serializes its chips with "; ", so reading that text back must split it too.
        EnsureApplication();
        var box = new TokenizedAddressBox { AddressText = "ann@example.com; Ben Baker <ben@example.com>" };

        Assert.Equal(new[] { "ann@example.com", "ben@example.com" }, box.GetChips().Select(c => c.EmailAddress));
        Assert.Equal(string.Empty, box.InputBox.Text);
    }

    [StaFact]
    public void Pasting_MultiLineText_IsFlattenedToSeparatedAddresses()
    {
        // #980: a single-line TextBox keeps only the first line of a multi-line paste.
        EnsureApplication();
        var source = new DataObject(DataFormats.UnicodeText, "ann@example.com\r\nben@example.com\r\ncat@example.com");
        var args = new DataObjectPastingEventArgs(source, isDragDrop: false, DataFormats.UnicodeText);

        TokenizedAddressBox.InputBox_Pasting(new object(), args);

        Assert.Equal("ann@example.com; ben@example.com; cat@example.com",
            args.DataObject.GetData(DataFormats.UnicodeText) as string);
    }

    [StaFact]
    public void PastingHandler_IsWiredToTheInputBox()
    {
        // Raise the real Pasting event on the input, as every paste gesture does.
        EnsureApplication();
        var box = new TokenizedAddressBox();
        var source = new DataObject(DataFormats.UnicodeText, "ann@example.com\nben@example.com");
        var args = new DataObjectPastingEventArgs(source, isDragDrop: false, DataFormats.UnicodeText);

        box.InputBox.RaiseEvent(args);

        Assert.Equal("ann@example.com; ben@example.com", args.DataObject.GetData(DataFormats.UnicodeText) as string);
    }

    [StaFact]
    public void Leftover_text_keeps_the_caret_at_its_end()
    {
        EnsureApplication();
        var box = new TokenizedAddressBox();

        box.InputBox.Text = "ann@example.com; Ford, Kelly";
        DoEvents();

        Assert.Equal(new[] { "ann@example.com" }, box.GetChips().Select(c => c.EmailAddress));
        Assert.Equal("Ford, Kelly", box.InputBox.Text);
        Assert.Equal(box.InputBox.Text.Length, box.InputBox.CaretIndex);
    }

    [StaFact]
    public void Pasting_SingleLineText_IsLeftAlone()
    {
        EnsureApplication();
        var source = new DataObject(DataFormats.UnicodeText, "ann@example.com; ben@example.com");
        var args = new DataObjectPastingEventArgs(source, isDragDrop: false, DataFormats.UnicodeText);

        TokenizedAddressBox.InputBox_Pasting(new object(), args);

        Assert.Same(source, args.DataObject);
    }

    [StaFact]
    public void FlattenedMultiLinePaste_CommitsEveryAddress()
    {
        EnsureApplication();
        var box = new TokenizedAddressBox();

        box.InputBox.Text = QuickMail.Helpers.AddressInput.FlattenForSingleLine("ann@example.com\nben@example.com\ncat@example.com");
        DoEvents();

        Assert.Equal(new[] { "ann@example.com", "ben@example.com", "cat@example.com" },
            box.GetChips().Select(c => c.EmailAddress));
    }

    /// <summary>Delegates to the shared host: the Application must live on a thread that outlives
    /// the run, not on whichever [StaFact] thread happened to be first (issue #211).</summary>
    private static void EnsureApplication() => WpfTestHost.EnsureApplication();

    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new System.Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
