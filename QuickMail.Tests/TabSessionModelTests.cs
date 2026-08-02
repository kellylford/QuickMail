using System;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tab session model/VM behaviour deferred from PR #38 (issue #40): title derivation for message
/// tabs, the <see cref="TabSessionViewModel"/> defaults, and the close-request event.
///
/// Titles matter beyond the visual strip: <c>Title</c> is what the tab announces, and it is what
/// <see cref="MainViewModel"/> reads back into its "Opened tab: …" / "Closed tab: …" announcements.
/// A regression here is heard, not just seen.
/// </summary>
public class TabSessionModelTests
{
    private const int MaxTitleLength = 60;

    private static MailMessageSummary Summary(string? subject, string id = "1") => new()
    {
        MessageId  = id,
        AccountId  = Guid.NewGuid(),
        FolderName = "INBOX",
        Subject    = subject!,
    };

    /// <summary>Exposes the protected RequestClose() so the event contract can be exercised.</summary>
    private sealed class TestTab : TabSessionViewModel
    {
        public TestTab(TabSessionModel model) : base(model) { }
        public void RaiseClose() => RequestClose();
    }

    // ── Title truncation ─────────────────────────────────────────────────────────

    [Fact]
    public void Title_ShortSubject_IsUsedVerbatim()
    {
        var tab = new MessageTabViewModel(Summary("Lunch tomorrow?"));
        Assert.Equal("Lunch tomorrow?", tab.Title);
    }

    [Fact]
    public void Title_SubjectIsTrimmed()
    {
        var tab = new MessageTabViewModel(Summary("   padded   "));
        Assert.Equal("padded", tab.Title);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n ")]
    public void Title_BlankSubject_BecomesUntitled(string? subject)
    {
        var tab = new MessageTabViewModel(Summary(subject));
        Assert.Equal("Untitled", tab.Title);
    }

    [Fact]
    public void Title_ExactlyMaxLength_IsNotTruncated()
    {
        // Boundary: the truncation test is `> MaxTitleLength`, so 60 characters must survive intact.
        var subject = new string('a', MaxTitleLength);
        var tab = new MessageTabViewModel(Summary(subject));

        Assert.Equal(subject, tab.Title);
        Assert.DoesNotContain("…", tab.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Title_OverMaxLength_IsTruncatedWithEllipsis()
    {
        var subject = new string('a', MaxTitleLength + 40);
        var tab = new MessageTabViewModel(Summary(subject));

        Assert.Equal(new string('a', MaxTitleLength) + "…", tab.Title);
        // One ellipsis CHARACTER, not three periods — the strip budgets on character count.
        Assert.Equal(MaxTitleLength + 1, tab.Title.Length);
    }

    [Fact]
    public void Title_TrimHappensBeforeTruncation()
    {
        // Leading whitespace must not consume part of the 60-character budget.
        var subject = "   " + new string('b', MaxTitleLength);
        var tab = new MessageTabViewModel(Summary(subject));

        Assert.Equal(new string('b', MaxTitleLength), tab.Title);
        Assert.DoesNotContain("…", tab.Title, StringComparison.Ordinal);
    }

    // ── Title tracks the loaded detail ───────────────────────────────────────────

    [Fact]
    public void Detail_WhenSet_MarksLoadedAndRetitles()
    {
        var tab = new MessageTabViewModel(Summary("summary subject"));
        Assert.False(tab.IsLoaded);

        tab.Detail = new MailMessageDetail { Subject = "detail subject" };

        Assert.True(tab.IsLoaded);
        Assert.Equal("detail subject", tab.Title);
        Assert.Equal("detail subject", tab.Model.Tooltip);
    }

    [Fact]
    public void Detail_WithBlankSubject_FallsBackToSummarySubject()
    {
        var tab = new MessageTabViewModel(Summary("summary subject"));

        // Subject is declared non-nullable, but the fallback in OnDetailChanged exists precisely
        // because a server can hand back a detail with no subject. null! reproduces that.
        tab.Detail = new MailMessageDetail { Subject = null! };

        Assert.True(tab.IsLoaded);
        Assert.Equal("summary subject", tab.Title);
    }

    [Fact]
    public void Detail_LongSubject_IsTruncatedToo()
    {
        var tab = new MessageTabViewModel(Summary("short"));

        tab.Detail = new MailMessageDetail { Subject = new string('c', MaxTitleLength + 10) };

        Assert.Equal(new string('c', MaxTitleLength) + "…", tab.Title);
    }

    [Fact]
    public void Detail_SetToNull_DoesNotRetitleOrMarkLoaded()
    {
        var tab = new MessageTabViewModel(Summary("summary subject"));

        tab.Detail = null;

        Assert.False(tab.IsLoaded);
        Assert.Equal("summary subject", tab.Title);
    }

    // ── Model/VM sync and defaults ───────────────────────────────────────────────

    [Fact]
    public void Title_SetOnViewModel_WritesThroughToModel()
    {
        var tab = new MessageTabViewModel(Summary("original"));

        tab.Title = "renamed";

        Assert.Equal("renamed", tab.Model.Title);
    }

    [Fact]
    public void IsDirty_SetOnViewModel_WritesThroughToModel()
    {
        var tab = new MessageTabViewModel(Summary("s"));

        tab.IsDirty = true;

        Assert.True(tab.Model.IsDirty);
    }

    [Fact]
    public void MessageTab_Defaults_AreCleanAndCloseable()
    {
        var tab = new MessageTabViewModel(Summary("s"));

        Assert.False(tab.IsDirty);
        Assert.True(tab.CanClose);
        Assert.True(tab.CanCloseNow());
        Assert.Equal(TabKind.Message, tab.Model.Kind);
    }

    [Fact]
    public void MessageTab_CarriesMessageIdAsContentKey()
    {
        var summary = Summary("s", id: "uid-42");
        var tab = new MessageTabViewModel(summary);

        Assert.Equal("uid-42", tab.Model.ContentKey);
        Assert.Same(summary, tab.Summary);
    }

    [Fact]
    public void MessageListTab_IsPermanentAndNeverCloseable()
    {
        var tab = new MessageListTabViewModel();

        Assert.False(tab.CanClose);
        Assert.False(tab.CanCloseNow());
        Assert.Equal("Messages", tab.Title);
        Assert.Equal(TabKind.MessageList, tab.Model.Kind);
    }

    [Fact]
    public void ConstructorSeedsViewModelStateFromModel()
    {
        var tab = new TestTab(new TabSessionModel
        {
            Kind     = TabKind.Unknown,
            Title    = "seeded",
            IsDirty  = true,
            CanClose = false,
        });

        Assert.Equal("seeded", tab.Title);
        Assert.True(tab.IsDirty);
        Assert.False(tab.CanClose);
    }

    // ── CloseRequested ───────────────────────────────────────────────────────────

    [Fact]
    public void RequestClose_RaisesCloseRequestedWithItself()
    {
        var tab = new TestTab(new TabSessionModel { Kind = TabKind.Unknown, Title = "t" });
        TabSessionViewModel? raisedWith = null;
        tab.CloseRequested += t => raisedWith = t;

        tab.RaiseClose();

        Assert.Same(tab, raisedWith);
    }

    [Fact]
    public void RequestClose_WithNoSubscriber_DoesNotThrow()
    {
        var tab = new TestTab(new TabSessionModel { Kind = TabKind.Unknown, Title = "t" });

        tab.RaiseClose(); // must be a no-op, not a NullReferenceException

        Assert.Equal("t", tab.Title);
    }

    [Fact]
    public void CloseRequested_AfterUnsubscribe_IsNotRaised()
    {
        var tab = new TestTab(new TabSessionModel { Kind = TabKind.Unknown, Title = "t" });
        var count = 0;
        void Handler(TabSessionViewModel _) => count++;

        tab.CloseRequested += Handler;
        tab.RaiseClose();
        tab.CloseRequested -= Handler;
        tab.RaiseClose();

        Assert.Equal(1, count);
    }

    // ── CanCloseNow ──────────────────────────────────────────────────────────────

    [Fact]
    public void CanCloseNow_CleanTab_IsTrue()
    {
        var tab = new TestTab(new TabSessionModel { Kind = TabKind.Unknown }) { IsDirty = false };
        Assert.True(tab.CanCloseNow());
    }

    [Fact]
    public void CanCloseNow_DirtyCloseableTab_IsFalse()
    {
        // The case the confirm-on-close prompt exists for.
        var tab = new TestTab(new TabSessionModel { Kind = TabKind.Unknown })
        {
            IsDirty  = true,
            CanClose = true,
        };
        Assert.False(tab.CanCloseNow());
    }
}
