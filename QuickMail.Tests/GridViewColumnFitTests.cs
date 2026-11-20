using System.Linq;
using System.Windows;
using System.Windows.Controls;
using QuickMail.Helpers;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The calendar agenda's columns are sized to their content, with Subject taking the rest (issue #547).
/// Fixed widths cut "11:00 AM–12:00 PM" to "11:00 AM–12:00" — a whole and wrong time — while an empty
/// filler header sat at the right holding the width it needed.
/// </summary>
public class GridViewColumnFitTests
{
    [Fact]
    public void WithRoom_EveryColumnGetsWhatItNeeds_AndTheFillColumnTakesTheRest()
    {
        var widths = GridViewColumnFit.Fit([0, 210, 110, 150], fillIndex: 0, available: 800, fillMinWidth: 130);

        Assert.Equal([330, 210, 110, 150], widths);
        Assert.Equal(800, widths.Sum());   // nothing left over for an empty filler column
    }

    [Fact]
    public void TheFillColumnsOwnNeededWidthIsIgnored()
    {
        var widths = GridViewColumnFit.Fit([5000, 100], fillIndex: 0, available: 400, fillMinWidth: 50);

        Assert.Equal([300, 100], widths);
    }

    [Fact]
    public void FillColumnBelowItsMinimum_TheOthersShrinkInProportion()
    {
        // Others need 400; only 300 is available after Subject's minimum of 100.
        var widths = GridViewColumnFit.Fit([0, 200, 100, 100], fillIndex: 0, available: 400, fillMinWidth: 100);

        Assert.Equal(100, widths[0]);
        Assert.Equal(150, widths[1], 6);
        Assert.Equal(75, widths[2], 6);
        Assert.Equal(75, widths[3], 6);
        Assert.Equal(400, widths.Sum(), 6);
    }

    [Fact]
    public void FillMinimumWiderThanTheWholeList_IsCappedAtTheList()
    {
        var widths = GridViewColumnFit.Fit([0, 100], fillIndex: 0, available: 80, fillMinWidth: 200);

        Assert.Equal([80, 0], widths);
    }

    [Fact]
    public void NoWidthYet_GivesZeros_RatherThanNegativeWidths()
    {
        Assert.All(GridViewColumnFit.Fit([0, 100, 50], 0, available: 0, fillMinWidth: 100), w => Assert.Equal(0, w));
        Assert.All(GridViewColumnFit.Fit([0, 100, 50], 0, available: -20, fillMinWidth: 100), w => Assert.Equal(0, w));
    }

    [Fact]
    public void FillColumnNeedNotBeFirst()
    {
        var widths = GridViewColumnFit.Fit([100, 0, 50], fillIndex: 1, available: 400, fillMinWidth: 10);

        Assert.Equal([100, 250, 50], widths);
    }

    [Fact]
    public void AFillIndexOutsideTheColumns_Throws()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => GridViewColumnFit.Fit([1, 2], 2, 100, 10));
    }
}

/// <summary>
/// When a window is too narrow even for content-sized columns, the agenda's cells must trim with an
/// ellipsis, so a cut-off time is visibly cut off (#547). The cells are the plain TextBlocks
/// DisplayMemberBinding generates, so the ellipsis comes from an implicit TextBlock style in the list's
/// resources — guard that it is still there.
/// </summary>
[Collection("WpfTests")]
public class CalendarAgendaTrimmingTests
{
    [StaFact]
    public void CalendarListCellsTrimWithAnEllipsis()
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
            var list = window.FindName("CalendarList") as ListView;
            Assert.NotNull(list);

            var style = list!.Resources[typeof(TextBlock)] as Style;
            Assert.NotNull(style);
            var trimming = style!.Setters.OfType<Setter>()
                .SingleOrDefault(s => s.Property == TextBlock.TextTrimmingProperty);
            Assert.NotNull(trimming);
            Assert.Equal(TextTrimming.CharacterEllipsis, trimming!.Value);
        }
        finally { window.Close(); }
    }
}
