// Real mouse clicks - SendInput, at real screen coordinates, against a real MainWindow.
//
// Everything else that covers the mouse work stops short of the input pipeline. MouseActivationTests
// asserts the row-resolution logic and raises Mouse.MouseUpEvent on an element it chose itself, and
// its wiring tests read MainWindow's source as text. Both are worth having and neither can answer
// the question the folder bug (#601) actually turned on: does a click on that pixel end up running
// that handler? Every piece was correct there and clicking a folder still did nothing.
//
// So these move the pointer to a row, let Windows hit-test it, and assert on what the ViewModel did.
// A press the TreeViewItem marks handled, a template that swallows the button-up, a row whose
// container never realizes - all of it fails here and nowhere else.
//
// Gated behind QUICKMAIL_RUN_INPUT_TESTS=1 (CI sets it; quickmail.yml fails the job if these did not
// run there). They move the machine's real pointer, so every click first confirms that Windows puts
// our window at that screen point and that WPF's own input hit test finds the intended element
// there - a scaling or layout mismatch fails the assertion instead of clicking whatever was really
// at that point. Press and release are paired in a finally, so a failed assertion mid-gesture can
// never leave the machine's button held down.
//
// THE WINDOW RUNS ON ITS OWN THREAD, with a real Dispatcher.Run() message loop, and the test drives
// it from outside. That is not incidental. The obvious harness - show the window on the test's own
// thread and pump it with DispatcherFrame/PushFrame between input events - is a nested message loop
// running over a live WebView2, which is the exact shape CLAUDE.md documents as a hard dispatcher
// deadlock when a screen reader is active. It duly deadlocked: the suite hung with no output on the
// first machine it was run on. Real input needs a real message loop, so give it one.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

// Loads a real MainWindow, so it belongs in the collection that serializes window-loading tests:
// two of them constructing a MainWindow at once race inside XAML loading (#590).
[Collection("WpfTests")]
public class MouseClickInputTests
{
    private static readonly Guid AccountId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    // -- The folder tree ------------------------------------------------------

    [StaFact(Skip = InputTests.MouseSkipReason,
             SkipUnless = nameof(InputTests.Enabled), SkipType = typeof(InputTests))]
    public void ClickingAFolderRow_OpensThatFolder()
    {
        // The whole chain, for real: hit-test -> TreeViewItem selects on the press -> nothing marks
        // the button-up handled -> the handler on the TreeView runs -> SelectFolderCommand loads it.
        Run(app =>
        {
            var before = app.Store.FolderLoads;

            app.ClickFolderLabel(app.Inbox);

            app.WaitUntil(() => app.SelectedFolderIs(app.Inbox),
                          "clicking Inbox never opened it; tree selection is " + app.TreeSelection);
            Assert.Equal(before + 1, app.Store.FolderLoads);
        });
    }

    [StaFact(Skip = InputTests.MouseSkipReason,
             SkipUnless = nameof(InputTests.Enabled), SkipType = typeof(InputTests))]
    public void ClickingAChildFolderRow_OpensTheChildNotItsParent()
    {
        // Child rows are nested inside the parent's container, so a walk that stops at the outermost
        // row would open Inbox whenever a subfolder is clicked.
        Run(app =>
        {
            app.ClickFolderLabel(app.Subfolder);

            app.WaitUntil(() => app.SelectedFolderIs(app.Subfolder),
                          "clicking the subfolder did not open it");
        });
    }

    [StaFact(Skip = InputTests.MouseSkipReason,
             SkipUnless = nameof(InputTests.Enabled), SkipType = typeof(InputTests))]
    public void ClickingTheExpanderChevron_CollapsesTheBranchWithoutOpeningTheFolder()
    {
        // The chevron is chrome with its own action. It also does not move the selection, which is
        // why a handler that read SelectedItem would have opened whatever was selected beforehand.
        Run(app =>
        {
            var before = app.Store.FolderLoads;
            var open = app.OpenFolder;

            app.ClickFolderChevron(app.Inbox);

            app.WaitUntil(() => !app.Inbox.IsExpanded, "the chevron did not collapse the branch");
            app.Settle();
            Assert.Equal(open, app.OpenFolder);
            Assert.Equal(before, app.Store.FolderLoads);
        });
    }

    [StaFact(Skip = InputTests.MouseSkipReason,
             SkipUnless = nameof(InputTests.Enabled), SkipType = typeof(InputTests))]
    public void ClickingTheEmptySpaceBelowTheRows_OpensNothing()
    {
        // Inside the tree, on no row. The flat message list used to re-open the selected message
        // here, which in Window mode meant a second window from a click on blank space.
        Run(app =>
        {
            var before = app.Store.FolderLoads;
            var open = app.OpenFolder;

            app.ClickBelowTheFolderRows();
            app.Settle();

            Assert.Equal(open, app.OpenFolder);
            Assert.Equal(before, app.Store.FolderLoads);
        });
    }

    [StaFact(Skip = InputTests.MouseSkipReason,
             SkipUnless = nameof(InputTests.Enabled), SkipType = typeof(InputTests))]
    public void DoubleClickingAFolderRow_OpensItOnce()
    {
        // Two clicks deliver two button-ups. Unpaired, the second one started a second load - and on
        // the account list a second connect, which cancelled the first and left "Connection
        // cancelled." on an account that had just connected.
        Run(app =>
        {
            var before = app.Store.FolderLoads;

            app.ClickFolderLabel(app.Inbox, clicks: 2);

            app.WaitUntil(() => app.SelectedFolderIs(app.Inbox),
                          "the double-click never opened the folder at all");
            app.Settle();
            Assert.Equal(before + 1, app.Store.FolderLoads);
        });
    }

    // -- The message list -----------------------------------------------------

    [StaFact(Skip = InputTests.MouseSkipReason,
             SkipUnless = nameof(InputTests.Enabled), SkipType = typeof(InputTests))]
    public void ClickingAMessageRow_OpensThatMessage()
    {
        Run(app =>
        {
            app.ClickMessageRow(app.Messages[0]);

            app.WaitUntil(() => app.IsMessageOpen, "clicking a message did not open it");
            Assert.True(app.SelectedMessageIs(app.Messages[0]));
            Assert.NotNull(app.OpenDetail);
        });
    }

    [StaFact(Skip = InputTests.MouseSkipReason,
             SkipUnless = nameof(InputTests.Enabled), SkipType = typeof(InputTests))]
    public void DraggingAcrossMessageRows_OpensNothing()
    {
        // Press on the first message, release over the third. The release used to activate whatever
        // row it landed on, opening a message the user never clicked - and opening one reduces an
        // Extended selection to that single row, so a Delete aimed at several deleted one.
        //
        // The selection assertion is one row, not three, and that is not a slip: a WPF ListBox has no
        // native drag-select, so the press selects its own row and dragging adds nothing. What is
        // under test here is the release, which must activate neither row.
        Run(app =>
        {
            var openBefore = app.OpenDetail;

            app.DragAcrossMessages(app.Messages[0], app.Messages[2]);
            app.Settle();

            Assert.False(app.IsMessageOpen, "the drag opened a message.");
            Assert.Same(openBefore, app.OpenDetail);
            Assert.Equal(1, app.SelectedMessageCount);
        });
    }

    [StaFact(Skip = InputTests.MouseSkipReason,
             SkipUnless = nameof(InputTests.Enabled), SkipType = typeof(InputTests))]
    public void CtrlClickingAnotherMessage_ExtendsTheSelectionWithoutOpeningIt()
    {
        // The message list is Extended-selection. Ctrl+clicking five messages to delete them used to
        // open all five on the way past - five windows, in Window mode.
        Run(app =>
        {
            // A plain click first: it opens one message, and it brings the window to the foreground,
            // which is what lets the process see the Ctrl that follows.
            app.ClickMessageRow(app.Messages[0]);
            app.WaitUntil(() => app.IsMessageOpen, "the first click did not open a message");
            var opened = app.OpenDetail;

            app.ClickMessageRow(app.Messages[2], withControl: true);
            app.Settle();

            Assert.Equal(2, app.SelectedMessageCount);
            // Opening the second message would replace the loaded detail; the reading pane must
            // still be showing the one message the user actually opened.
            Assert.Same(opened, app.OpenDetail);
        });
    }

    // -- Harness --------------------------------------------------------------

    private static void Run(Action<AppUnderMouse> test)
    {
        // A locked screen or a UAC prompt puts the secure desktop in front: input goes there instead,
        // and the test would fail for a reason that has nothing to do with the code.
        Assert.SkipUnless(RealMouse.DesktopIsInteractive(),
                          "the secure desktop is up (a locked session, or a UAC prompt); real input " +
                          "cannot reach the window.");

        var cursor = RealMouse.CursorPosition;
        var app = new AppUnderMouse();
        try
        {
            test(app);
        }
        finally
        {
            // Backstop for the machine's own state: whatever went wrong, the button and the modifier
            // must not still be down when this returns, or every test after it runs against a
            // desktop stuck mid-gesture.
            RealMouse.ReleaseLeft();
            RealMouse.ReleaseControl();
            app.Dispose();
            RealMouse.RestoreCursor(cursor);
        }
    }

    /// <summary>
    /// A real MainWindow running its own message loop on its own STA thread, driven from the test
    /// thread by real input. Every read of the window or the ViewModel goes through <see cref="On"/>,
    /// since they belong to that thread.
    /// </summary>
    internal sealed class AppUnderMouse : IDisposable
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

        private readonly Thread _thread;
        private readonly Dispatcher _dispatcher;
        private MainWindow _window = null!;
        private MainViewModel _vm = null!;
        private int _moves;
        private int _downs;

        /// <summary>Mouse input the window itself saw, for failure messages.</summary>
        public string InputSeen =>
            $"{Volatile.Read(ref _moves)} moves, {Volatile.Read(ref _downs)} presses; " +
            On(() => RealMouse.WhyNotReceivingInput(new WindowInteropHelper(_window).Handle));

        public CountingStore Store { get; } = new();
        public AccountModel TheAccount { get; } = new()
        {
            Id = AccountId, AccountName = "Work", Username = "kelly@example.com",
        };
        public FolderTreeNode Inbox { get; } = BuildFolders();
        public FolderTreeNode Subfolder => Inbox.Children[0];
        public List<MailMessageSummary> Messages { get; } = BuildMessages();

        public AppUnderMouse()
        {
            // The Application itself stays on the shared host thread (issue #211); only the window
            // is ours.
            WpfTestHost.EnsureStyles("AccessibleStyles", "ThemedControls");

            using var ready = new ManualResetEventSlim();
            Dispatcher? dispatcher = null;
            Exception? failure = null;

            _thread = new Thread(() =>
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                try { Build(); }
                catch (Exception ex) { failure = ex; }
                finally { ready.Set(); }

                if (failure is null) Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "real-mouse app thread",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait();

            if (failure is not null)
                throw new InvalidOperationException("Could not stand up the window under test.", failure);

            _dispatcher = dispatcher!;

            // Let the ViewModel's own start-up work run out before seeding over it, and again after,
            // so the rows are realized by the time the first click asks where they are.
            Settle();
            On(Seed);
            Settle();
        }

        public void Dispose()
        {
            try { _dispatcher.Invoke(() => _window.Close(), DispatcherPriority.Send, CancellationToken.None, Patience); }
            catch (TimeoutException) { /* falling through to shutdown is the point */ }
            _dispatcher.InvokeShutdown();
            _thread.Join(Patience);
        }

        // -- Building and seeding ---------------------------------------------

        private void Build()
        {
            var imap = new StubImapMailService();
            // NOT StubAccountService, which owns no accounts. Start-up opens the Account Manager as
            // a MODAL dialog when there are none, and a modal dialog disables its owner - so the
            // window under test receives no mouse input at all, while still being the window
            // WindowFromPoint names and still hit-testing correctly in WPF. Every click then looks
            // exactly like a click the app ignored.
            var accounts = new OneAccountService(TheAccount);
            var creds = new StubCredentialService();
            var config = new StubConfigService();
            var registry = new StubCommandRegistry();

            _vm = new MainViewModel(imap, accounts, creds, Store, new StubOAuthService(),
                new StubSyncService(), config, registry, new StubViewService(), new StubRuleService(),
                new StubSmtpService(), rowLayoutService: new StubRowLayoutService());

            // Before the window exists, because its start-up reads _vm.Accounts directly and opens
            // the modal Account Manager over itself when that is empty - the ViewModel does not fill
            // it in until later. In the real app it is App that has already loaded the accounts by
            // this point.
            _vm.Accounts.Add(TheAccount);
            _vm.SelectedAccount = TheAccount;

            _window = new MainWindow(_vm, new StubSmtpService(), accounts, creds, imap,
                new StubOAuthService(), registry, new StubContactService(), config, Store,
                new StubViewService(), new StubRuleService(), new StubTemplateService(),
                new StubFeatureGate());

            // Topmost so nothing can sit between the pointer and the row it is aiming at. Positioned
            // rather than centred so the whole window is on the primary monitor whatever else is
            // open, and sized well past MinWidth so both panes have room for real rows.
            _window.WindowStartupLocation = WindowStartupLocation.Manual;
            _window.Left = 40;
            _window.Top = 40;
            _window.Width = 1100;
            _window.Height = 700;
            _window.Topmost = true;
            // Counted at the window, before anything can handle them: this is what separates "the
            // input never reached the window" from "it reached it and nothing acted on it". The two
            // are indistinguishable at the ViewModel, and the first one is not a bug in the app.
            _window.PreviewMouseMove += (_, _) => Interlocked.Increment(ref _moves);
            _window.PreviewMouseDown += (_, _) => Interlocked.Increment(ref _downs);

            _window.Show();
            _window.Activate();
            _window.UpdateLayout();
        }

        /// <summary>
        /// Puts the folders and messages in once the window has settled. Seeding while building does
        /// not survive: the ViewModel rebuilds its own folder tree and clears the message list on the
        /// turns that follow construction, so nodes seeded earlier are no longer the ones the tree is
        /// showing - and every row lookup then fails with "no row was realized".
        /// </summary>
        private void Seed()
        {
            // SelectMessageAsync returns early without a selected account, so the message clicks
            // would otherwise pass for the wrong reason.
            if (!_vm.Accounts.Contains(TheAccount)) _vm.Accounts.Add(TheAccount);
            _vm.SelectedAccount = TheAccount;

            _vm.FolderTree = [Inbox];
            _vm.Messages.Clear();
            foreach (var message in Messages) _vm.Messages.Add(message);
            _window.UpdateLayout();

            // The seed is what every row lookup depends on. If the ViewModel rebuilt over it, say so
            // here rather than letting it surface later as a mouse problem.
            Assert.True(FolderTree.Items.Count == 1,
                        $"the folder tree shows {FolderTree.Items.Count} rows, not the one seeded - " +
                        "the ViewModel rebuilt it after seeding.");
            Assert.True(MessageList.Items.Count == Messages.Count,
                        $"the message list shows {MessageList.Items.Count} rows, not the " +
                        $"{Messages.Count} seeded - the ViewModel replaced them after seeding.");
        }

        // -- Crossing to the window's thread ----------------------------------

        /// <summary>Runs <paramref name="read"/> on the window's thread and returns its result.</summary>
        public T On<T>(Func<T> read) =>
            _dispatcher.Invoke(read, DispatcherPriority.Send, CancellationToken.None, Patience);

        public void On(Action act) =>
            _dispatcher.Invoke(act, DispatcherPriority.Send, CancellationToken.None, Patience);

        /// <summary>
        /// Returns once the window's own message loop has processed everything queued - including the
        /// input just sent to it. No nested frame: this waits on that thread's loop, it does not run
        /// one here.
        /// </summary>
        public void Idle() =>
            _dispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle, CancellationToken.None, Patience);

        /// <summary>
        /// Returns once input just sent has actually been delivered and processed. <c>SendInput</c>
        /// only queues: the input reaches the window when Windows next hands it to that thread, which
        /// can be after the dispatcher has already gone idle. Waiting for idle alone therefore races
        /// the input it is supposed to be waiting for - and reads back a window that has not seen the
        /// click yet.
        /// </summary>
        private void Delivered()
        {
            Idle();
            Thread.Sleep(30);
            Idle();
        }

        /// <summary>
        /// Lets everything already in flight finish, for the assertions that something did NOT
        /// happen - which cannot be waited for, only given time to.
        /// </summary>
        public void Settle()
        {
            for (var i = 0; i < 20; i++)
            {
                Idle();
                Thread.Sleep(10);
            }
        }

        /// <summary>
        /// Waits until <paramref name="done"/> holds. The click handlers are async void, so the work
        /// they start finishes on later turns of the window's loop - there is nothing to await.
        /// </summary>
        public void WaitUntil(Func<bool> done, string what)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                Idle();
                if (done()) return;
                Thread.Sleep(10);
            }
            Assert.True(done(), $"{what} (waited 5s; the window saw {InputSeen}).");
        }

        // -- What the tests assert on -----------------------------------------

        public bool SelectedFolderIs(FolderTreeNode? node) =>
            On(() => ReferenceEquals(_vm.SelectedFolder, node?.Folder));

        /// <summary>
        /// Compared before and after a gesture that must not open anything. Not "is it null": start-up
        /// selects a folder of its own, so nothing here begins with none open.
        /// </summary>
        public string OpenFolder => On(() => _vm.SelectedFolder?.FullName ?? "none");

        /// <summary>Which message pane is actually showing, for failure messages.</summary>
        public string PaneState => On(() =>
            $"view={_vm.ViewMode}, MessageList={MessageList.Visibility}/{MessageList.Items.Count} rows, " +
            $"ConversationTree={Find<TreeView>("ConversationTree").Visibility}, " +
            $"ToGroupTree={Find<TreeView>("ToGroupTree").Visibility}, " +
            $"SenderGroupTree={Find<TreeView>("SenderGroupTree").Visibility}, " +
            $"isToView={_vm.IsToView}");

        public bool SelectedMessageIs(MailMessageSummary message) =>
            On(() => ReferenceEquals(_vm.SelectedMessage, message));

        public bool IsMessageOpen => On(() => _vm.IsMessageOpen);

        /// <summary>
        /// The loaded message detail, compared by reference. Opening a message replaces it with a
        /// fresh object, so "is this still the same one" answers "did that gesture open anything" -
        /// which counting store loads cannot: the app prefetches the details of the whole visible
        /// list when a folder opens, so the count moves without any message being opened.
        /// </summary>
        public object? OpenDetail => On(() => (object?)_vm.MessageDetail);

        /// <summary>
        /// What the tree itself thinks is selected. A click that WPF processed at all moves this -
        /// the container selects on the button-down - so it separates "the input never arrived" from
        /// "it arrived and the handler did nothing", which fail identically at the ViewModel.
        /// </summary>
        public string TreeSelection => On(() => (FolderTree.SelectedItem as FolderTreeNode)?.Label ?? "none");

        public int SelectedMessageCount => On(() => MessageList.SelectedItems.Count);

        // -- Clicking ---------------------------------------------------------

        public void ClickFolderLabel(FolderTreeNode node, int clicks = 1) =>
            Click(() => LabelOf(RowFor(node)), clicks);

        public void ClickFolderChevron(FolderTreeNode node) => Click(() =>
        {
            var row = RowFor(node);
            var chevron = row.Template.FindName("Expander", row) as ToggleButton;
            Assert.True(chevron is not null, "the folder row template no longer has an Expander.");
            return chevron!;
        });

        public void ClickMessageRow(MailMessageSummary message, bool withControl = false) =>
            Click(() => RowFor(message), withControl: withControl);

        /// <summary>
        /// Moves the pointer to the centre of the element, confirms WPF reports it under the cursor,
        /// and only then clicks. That confirmation is what makes a real click safe to run on
        /// someone's desktop: if the coordinates do not land where the test thinks they do - a
        /// display-scaling mismatch, a window in front, a row scrolled out of view - the test fails
        /// without ever pressing the button.
        /// </summary>
        private void Click(Func<FrameworkElement> resolve, int clicks = 1, bool withControl = false)
        {
            var (element, point, what) = On(() =>
            {
                var e = resolve();
                return (e, CentreOf(e), Describe(e));
            });

            RealMouse.MoveTo(point);
            Delivered();
            On(() => AssertPointerIsOver(element, point, what));

            if (withControl) RealMouse.HoldControl();
            try
            {
                for (var i = 0; i < clicks; i++)
                {
                    RealMouse.PressLeft();
                    try { Delivered(); }
                    finally { RealMouse.ReleaseLeft(); }
                    Delivered();
                }
            }
            finally
            {
                // A button or key left down by a failed test stays down on the machine.
                if (withControl) RealMouse.ReleaseControl();
            }
        }

        /// <summary>Presses on one message row, moves to another, and releases there.</summary>
        public void DragAcrossMessages(MailMessageSummary from, MailMessageSummary to)
        {
            var (element, start, what) = On(() =>
            {
                var e = RowFor(from);
                return (e, CentreOf(e), Describe(e));
            });
            var end = On(() => CentreOf(RowFor(to)));

            RealMouse.MoveTo(start);
            Delivered();
            On(() => AssertPointerIsOver(element, start, what));

            RealMouse.PressLeft();
            try
            {
                // Stepped, so the list sees the rows in between the way it does under a real drag.
                for (var step = 1; step <= 4; step++)
                {
                    RealMouse.MoveTo(new Point(
                        start.X + ((end.X - start.X) * step / 4.0),
                        start.Y + ((end.Y - start.Y) * step / 4.0)));
                    Delivered();
                }
            }
            finally
            {
                // Never leave the button down. A failed assertion between the press and the release
                // would not be a test failure any more - it would be the machine stuck mid-drag, and
                // the rest of the run wedged behind it.
                RealMouse.ReleaseLeft();
            }
            Delivered();
        }

        /// <summary>Clicks inside the folder tree but below its last row - on no row at all.</summary>
        public void ClickBelowTheFolderRows()
        {
            var point = On(() =>
            {
                var tree = FolderTree;
                var topLeft = tree.PointToScreen(new Point(0, 0));
                var bottomRight = tree.PointToScreen(new Point(tree.ActualWidth, tree.ActualHeight));
                // A third of the way across keeps it clear of the scroll bar, and off the bottom edge.
                return new Point(topLeft.X + ((bottomRight.X - topLeft.X) / 3), bottomRight.Y - 8);
            });

            RealMouse.MoveTo(point);
            Delivered();
            On(() => AssertPointerIsOver(FolderTree, point, "the empty space below the last row"));

            RealMouse.PressLeft();
            try { Delivered(); }
            finally { RealMouse.ReleaseLeft(); }
            Delivered();
        }

        /// <summary>
        /// Confirms the pointer really is over the thing the test is aiming at, before any button is
        /// pressed. Two questions, because they fail differently: Windows is asked whether OUR window
        /// is the one at that screen point (another window in front, or the window somewhere other
        /// than where it was put), and WPF is asked which visual is topmost there (a row scrolled out
        /// of view, a pane that is not the one the test thinks is showing, a scaling mismatch).
        ///
        /// <para>Deliberately not <c>Mouse.DirectlyOver</c>, which reads as empty here: this window
        /// runs on its own thread and is never activated, and WPF's mouse-over state only fills in
        /// once the window has processed mouse input of its own. Hit-testing asks the same question
        /// of the same visual tree without depending on that.</para>
        /// </summary>
        private void AssertPointerIsOver(DependencyObject target, Point point, string what)
        {
            var expectedWindow = new WindowInteropHelper(_window).Handle;
            var actualWindow = RealMouse.TopLevelWindowAt(point);
            Assert.True(actualWindow == expectedWindow,
                $"aimed at {point} for {what}, but Windows reports another top-level window there " +
                $"(got {actualWindow:X}, expected {expectedWindow:X}). No click was sent - it would " +
                "have gone to whatever is really in front.");

            // InputHitTest, not VisualTreeHelper.HitTest: the latter is a pure geometric test over
            // the visual tree and happily returns visuals inside a COLLAPSED subtree, still carrying
            // the bounds they were last arranged with. Here that meant a hidden group tree answering
            // for the message list underneath it. InputHitTest asks the question a click asks.
            var hit = _window.InputHitTest(_window.PointFromScreen(point)) as DependencyObject;
            Assert.True(hit is not null && IsWithin(hit, target),
                $"aimed at {point} for {what}, and the cursor reports {RealMouse.CursorPosition}, " +
                $"but the topmost visual there is '{DescribeChain(hit)}' ({PaneState}). No click was " +
                "sent. The element " +
                "is not where layout says it is - a row scrolled out of view, a pane other than the " +
                "one expected, or a display-scaling mismatch in the test host.");
        }

        private static Point CentreOf(FrameworkElement element)
        {
            Assert.True(element.ActualWidth > 0 && element.ActualHeight > 0,
                        Describe(element) + " has no size, so there is nothing to click.");
            return element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        }

        private static bool IsWithin(object? candidate, DependencyObject target)
        {
            var node = candidate as DependencyObject;
            while (node is not null)
            {
                if (ReferenceEquals(node, target)) return true;
                // A Run has no visual parent - VisualTreeHelper throws on content elements - so fall
                // back to the logical tree, the same hop MouseActivation makes.
                node = node is Visual
                    ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            }
            return false;
        }

        // -- Finding rows (window thread only) --------------------------------

        private TreeView FolderTree => Find<TreeView>("FolderList");
        private ListView MessageList => Find<ListView>("MessageList");

        private TreeViewItem RowFor(FolderTreeNode node)
        {
            // A child row's container belongs to its parent row, not to the tree.
            ItemsControl parent = ReferenceEquals(node, Inbox) ? FolderTree : RowFor(Inbox);
            parent.UpdateLayout();
            var row = parent.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
            Assert.True(row is not null, "no row was realized for '" + node.Label + "'.");
            row!.ApplyTemplate();
            row.UpdateLayout();
            return row;
        }

        private ListViewItem RowFor(MailMessageSummary message)
        {
            MessageList.UpdateLayout();
            var row = MessageList.ItemContainerGenerator.ContainerFromItem(message) as ListViewItem;
            Assert.True(row is not null, "no row was realized for '" + message.Subject + "'.");
            row!.ApplyTemplate();
            row.UpdateLayout();
            return row;
        }

        /// <summary>The text inside a row - what a user actually aims at, and a content element.</summary>
        private static FrameworkElement LabelOf(TreeViewItem row)
        {
            var label = Descendant<TextBlock>(row);
            Assert.True(label is not null, "the row has no text to click on.");
            return label!;
        }

        private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                // Stop at a nested row: a parent's descendants include its children's whole
                // subtrees, and returning one of those would click the wrong row.
                if (child is TreeViewItem or ListBoxItem) continue;
                // Skip anything with no size - it cannot be clicked, and zero-height chrome would
                // otherwise be picked over the label.
                if (child is T hit && child is not FrameworkElement { ActualHeight: 0 }) return hit;
                if (Descendant<T>(child) is { } deeper) return deeper;
            }
            return null;
        }

        private T Find<T>(string name) where T : class
        {
            var found = _window.FindName(name) as T;
            Assert.True(found is not null, name + " is gone from MainWindow.xaml.");
            return found!;
        }

        // -- Fixture data -----------------------------------------------------

        private static FolderTreeNode BuildFolders()
        {
            var child = new FolderTreeNode
            {
                Label = "Projects",
                Folder = new MailFolderModel
                {
                    FullName = "INBOX/Projects", DisplayName = "Projects", AccountId = AccountId,
                },
            };
            var inbox = new FolderTreeNode
            {
                Label = "Inbox",
                IsExpanded = true,
                Folder = new MailFolderModel
                {
                    FullName = "INBOX", DisplayName = "Inbox", AccountId = AccountId,
                },
            };
            inbox.Children.Add(child);
            return inbox;
        }

        private static List<MailMessageSummary> BuildMessages() =>
        [
            .. Enumerable.Range(0, 5).Select(i => new MailMessageSummary
            {
                MessageId = (i + 1).ToString(),
                AccountId = AccountId,
                FolderName = "INBOX",
                From = "alice@example.com",
                Subject = "Message " + (i + 1),
                Date = new DateTimeOffset(2026, 8, 20, 10, i, 0, TimeSpan.Zero),
            }),
        ];

        /// <summary>
        /// The hit visual plus its named ancestors. A bare type name ("ScrollViewer") does not say
        /// which control it belongs to, and that is exactly the question when the wrong pane is in
        /// front.
        /// </summary>
        private static string DescribeChain(DependencyObject? hit)
        {
            var parts = new List<string> { Describe(hit) };
            var node = hit;
            for (var i = 0; i < 12 && node is not null; i++)
            {
                node = node is Visual
                    ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
                if (node is FrameworkElement { Name.Length: > 0 } named)
                    parts.Add(named.GetType().Name + " '" + named.Name + "'");
            }
            return string.Join(" in ", parts);
        }

        private static string Describe(object? element) => element switch
        {
            null => "nothing",
            FrameworkElement { Name.Length: > 0 } named => named.GetType().Name + " '" + named.Name + "'",
            _ => element.GetType().Name,
        };
    }

    /// <summary>
    /// An account service that already has one account, so start-up does not put its modal Account
    /// Manager over the window under test.
    /// </summary>
    private sealed class OneAccountService(AccountModel account) : IAccountService
    {
        public List<AccountModel> LoadAccounts() => [account];
        public void SaveAccounts(List<AccountModel> accounts) { }
        public void SetDefaultAccount(Guid accountId) { }
    }

    /// <summary>
    /// Counts folder loads, which is how these tests tell one activation from two - and "the click
    /// opened it" from "it was already open".
    /// </summary>
    internal sealed class CountingStore : StubLocalStoreService
    {
        private int _folderLoads;

        /// <summary>Read from the test thread while the window's thread writes it.</summary>
        public int FolderLoads => Volatile.Read(ref _folderLoads);

        public override Task<List<MailMessageSummary>> LoadFolderSummariesAsync(
            Guid accountId, string folderName, int? limit = null)
        {
            Interlocked.Increment(ref _folderLoads);
            return base.LoadFolderSummariesAsync(accountId, folderName, limit);
        }
    }
}
