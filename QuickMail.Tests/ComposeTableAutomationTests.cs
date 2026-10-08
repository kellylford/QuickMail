using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Issue #782: Enter after a table at the end of a compose message, with a screen reader
/// running, ended the process. WPF's Enter there adds a table row; the table's automation peer
/// kept its old list of cells, and a text-pattern query for the enclosing element of a range in
/// the new cell failed a WPF assertion that calls <c>Environment.FailFast</c>.
/// <para>
/// FailFast cannot be caught and kills whatever process it runs in, so the editing happens in a
/// child process — this test assembly re-launched to run <see cref="ComposeTableAutomationChild"/>
/// alone — and this test is the automation client, asking the child's editor for its selection's
/// enclosing element from outside, the way a screen reader does. The child exits 0 if it lived.
/// </para>
/// </summary>
// Serial: the test shows a window in another process and times itself against it.
[CollectionDefinition(nameof(ComposeTableAutomationTests), DisableParallelization = true)]
public sealed class ComposeTableAutomationCollection { }

[Collection(nameof(ComposeTableAutomationTests))]
public sealed class ComposeTableAutomationTests
{
    [Fact(Skip = ProcessTests.SkipReason,
          SkipUnless = nameof(ProcessTests.Enabled), SkipType = typeof(ProcessTests))]
    public void EnterAfterTable_EnclosingElementQueryDoesNotEndTheProcess()
    {
        var token = "QuickMail782-" + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, token + "-ready");
        using var go = new EventWaitHandle(false, EventResetMode.ManualReset, token + "-go");
        using var entered = new EventWaitHandle(false, EventResetMode.ManualReset, token + "-entered");
        using var done = new EventWaitHandle(false, EventResetMode.ManualReset, token + "-done");

        var exe = Path.Combine(AppContext.BaseDirectory, "QuickMail.Tests.exe");
        var psi = new ProcessStartInfo(exe,
            $"-method \"{typeof(ComposeTableAutomationChild).FullName}.{nameof(ComposeTableAutomationChild.EnterAfterTable)}\" -noLogo")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment[ComposeTableAutomationChild.EnvVar] = token;
        using var child = Process.Start(psi)!;
        var output = new System.Text.StringBuilder();
        child.OutputDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        child.ErrorDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        child.BeginOutputReadLine();
        child.BeginErrorReadLine();
        string Output() { lock (output) return output.ToString(); }

        try
        {
            Assert.True(WaitFor(ready, child, 120_000),
                "the child compose window never became ready:\n" + Output());

            var window = AutomationElement.RootElement.FindFirst(TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, child.Id));
            Assert.NotNull(window);
            var editor = window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "RichBodyBox"));
            Assert.NotNull(editor);
            var text = (TextPattern)editor.GetCurrentPattern(TextPattern.Pattern);

            // Before Enter: the caret is after the table, and the query answers.
            Assert.NotNull(text.GetSelection().Single().GetEnclosingElement());

            go.Set();
            Assert.True(WaitFor(entered, child, 60_000),
                "the child did not report pressing Enter:\n" + Output());

            // After Enter the caret is in the table's new row. This is the query that failed. Asked
            // ten times, not once: a screen reader re-queries as it reads, and the crash came on
            // whichever query first reached the stale cell, so every answer has to be safe.
            AutomationElement? enclosing = null;
            for (int i = 0; i < 10 && !child.HasExited; i++)
            {
                try { enclosing = text.GetSelection().Single().GetEnclosingElement(); }
                catch (ElementNotAvailableException)
                {
                    // The provider process is gone, or going: let it finish so the exit is seen.
                    child.WaitForExit(10_000);
                    break;
                }
                Thread.Sleep(50);
            }
            Assert.False(child.HasExited,
                $"the child process ended during the query (exit code {(child.HasExited ? child.ExitCode : 0)}):\n" + Output());
            Assert.NotNull(enclosing);
        }
        finally
        {
            go.Set();   // a child still waiting to press Enter must not sit out its 90 seconds
            done.Set();
            if (!child.WaitForExit(60_000)) child.Kill(entireProcessTree: true);
        }
        child.WaitForExit();
        Assert.True(child.ExitCode == 0, $"child exit code {child.ExitCode}:\n{Output()}");
    }

    // True once the child signals; false if it exits first or the time runs out.
    private static bool WaitFor(WaitHandle signal, Process child, int milliseconds)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (signal.WaitOne(100)) return true;
            if (child.HasExited) return false;
        }
        return false;
    }
}

/// <summary>
/// The child half of <see cref="ComposeTableAutomationTests"/>: runs only in the process that
/// test launches, which passes it the names of the events the two sides signal each other with.
/// </summary>
[Collection("WpfTests")]
public sealed class ComposeTableAutomationChild
{
    public const string EnvVar = "QUICKMAIL_782_CHILD";

    public const string SkipReason =
        "Child half of ComposeTableAutomationTests; runs only when that test launches it.";

    /// <summary>Read by xUnit's <c>SkipUnless</c> at execution time. Must stay public and static.</summary>
    public static bool IsChild => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvVar));

    [StaFact(Skip = SkipReason, SkipUnless = nameof(IsChild), SkipType = typeof(ComposeTableAutomationChild))]
    public void EnterAfterTable()
    {
        var token = Environment.GetEnvironmentVariable(EnvVar)!;
        using var ready = EventWaitHandle.OpenExisting(token + "-ready");
        using var go = EventWaitHandle.OpenExisting(token + "-go");
        using var entered = EventWaitHandle.OpenExisting(token + "-entered");
        using var done = EventWaitHandle.OpenExisting(token + "-done");

        var vm = new ComposeViewModel(new StubSmtpService(), new StubAccountService(),
            new StubCredentialService(), new StubImapMailService(), new StubTemplateService());
        var window = new ComposeWindow(vm, new StubContactService(), new StubTemplateService(), new StubConfigService())
        {
            ConfirmSaveOnClose = null,
        };
        try
        {
            window.Show();
            vm.SetMode(ComposeMode.Html);
            var editor = window.FindName("RichBodyBox") as RichTextBox;
            Assert.NotNull(editor);
            RichTextDocumentConverter.LoadInto(editor!,
                "<p>Here are the numbers.</p><table><tr><td>One</td><td>Two</td></tr></table>");
            window.Activate();
            editor!.Focus();
            // The end of the document: after the table, which is the last block.
            editor.CaretPosition = editor.Document.ContentEnd;
            window.UpdateLayout();
            ready.Set();

            Pump(go);

            // What the Enter key does in the editor: the compose window's own handling first,
            // and the editor's paragraph break when that declines.
            if (!window.HandleRichEnter())
                EditingCommands.EnterParagraphBreak.Execute(null, editor);
            // The state the issue reported: Enter after the table gave it a new row, and the
            // caret is in it. Without this the test could pass by not reaching that state.
            Assert.IsType<TableCell>(editor.CaretPosition.Paragraph?.Parent);
            entered.Set();

            Pump(done);
        }
        finally
        {
            window.Close();
        }
    }

    // Runs the dispatcher, so the automation client's calls are served, until the event is set.
    private static void Pump(WaitHandle until)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            if (until.WaitOne(0) || DateTime.UtcNow > deadline) frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }
}
