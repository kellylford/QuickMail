using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The choice of what goes between a list row's spoken fields (#802): a period (default, what rows
/// always said), a comma, or a space. Reported by a user listening at a slow rate, for whom the
/// pause at every period across every row adds up. Punctuation inside a field is never touched,
/// and only the period choice ends the row with a period.
/// </summary>
[Collection("WpfTests")]
public class RowFieldSeparatorTests
{
    // ── what a row says ───────────────────────────────────────────────────────

    private static object?[] Message(string preview = "Learn about updates. No action is required today.") =>
        ["", "unread", false, "Microsoft 365", "Upcoming changes", preview, "10/13/2026",
         true, false, false, "", "", false, false];

    private static List<RowFieldSetting> MessageLayout() =>
        RowFieldCatalog.DefaultLayout(RowKind.Message);

    [Fact]
    public void Period_IsWhatRowsHaveAlwaysSaid()
    {
        var withDefault = RowSpeechBuilder.Build(RowKind.Message, Message(), MessageLayout());
        var withPeriod  = RowSpeechBuilder.Build(RowKind.Message, Message(), MessageLayout(),
                                                 separator: RowFieldSeparator.Period);

        Assert.Equal(withDefault, withPeriod);
        Assert.Equal(
            "unread. Microsoft 365. Upcoming changes. Learn about updates. No action is required today.. 10/13/2026.",
            withPeriod);
    }

    [Fact]
    public void Comma_JoinsWithCommasAndDropsTheFinalPeriod()
    {
        var text = RowSpeechBuilder.Build(RowKind.Message, Message("Lunch tomorrow?"), MessageLayout(),
                                          separator: RowFieldSeparator.Comma);

        Assert.Equal("unread, Microsoft 365, Upcoming changes, Lunch tomorrow?, 10/13/2026", text);
    }

    [Fact]
    public void Space_JoinsWithSpacesAndDropsTheFinalPeriod()
    {
        var text = RowSpeechBuilder.Build(RowKind.Message, Message("Lunch tomorrow?"), MessageLayout(),
                                          separator: RowFieldSeparator.Space);

        Assert.Equal("unread Microsoft 365 Upcoming changes Lunch tomorrow? 10/13/2026", text);
    }

    [Theory]
    [InlineData(RowFieldSeparator.Comma)]
    [InlineData(RowFieldSeparator.Space)]
    public void PunctuationInsideAFieldIsKept(RowFieldSeparator separator)
    {
        // The report was explicit: pauses from punctuation within a field are expected.
        var text = RowSpeechBuilder.Build(RowKind.Message, Message(), MessageLayout(), separator: separator);

        Assert.Contains("Learn about updates. No action is required today.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Comma_GivesWayToASpaceAfterAFieldsOwnPunctuation()
    {
        // "today., 10/13/2026" would put a comma straight after a full stop, from a choice made to
        // have less punctuation, not more.
        var period = RowSpeechBuilder.Build(RowKind.Message, Message(), MessageLayout(),
                                            separator: RowFieldSeparator.Comma);
        var question = RowSpeechBuilder.Build(RowKind.Message, Message("Lunch tomorrow?"), MessageLayout(),
                                              separator: RowFieldSeparator.Comma);

        Assert.Equal(
            "unread, Microsoft 365, Upcoming changes, Learn about updates. No action is required today. 10/13/2026",
            period);
        Assert.Equal("unread, Microsoft 365, Upcoming changes, Lunch tomorrow? 10/13/2026", question);
    }

    [Fact]
    public void Space_AfterAFieldsOwnPunctuationIsJustASpace()
    {
        var text = RowSpeechBuilder.Build(RowKind.Message, Message(), MessageLayout(),
                                          separator: RowFieldSeparator.Space);

        Assert.Equal(
            "unread Microsoft 365 Upcoming changes Learn about updates. No action is required today. 10/13/2026",
            text);
    }

    [Fact]
    public void LabelsStillWorkWithASpace()
    {
        var text = RowSpeechBuilder.Build(RowKind.Message, Message("Lunch tomorrow?"), MessageLayout(),
                                          showLabels: true, separator: RowFieldSeparator.Space);

        Assert.Equal("unread From: Microsoft 365 Subject: Upcoming changes Preview: Lunch tomorrow? Date: 10/13/2026", text);
    }

    [Theory]
    [InlineData(RowKind.Conversation)]
    [InlineData(RowKind.SenderGroup)]
    public void GroupRowsUseTheSeparatorToo(RowKind kind)
    {
        var values = RowFieldsViewModel.SampleValues(kind);
        var layout = RowFieldCatalog.DefaultLayout(kind);

        var period = RowSpeechBuilder.Build(kind, values, layout);
        var space  = RowSpeechBuilder.Build(kind, values, layout, separator: RowFieldSeparator.Space);

        Assert.Contains(". ", period, StringComparison.Ordinal);
        Assert.Equal(period.TrimEnd('.').Replace(". ", " ", StringComparison.Ordinal), space);
    }

    [Fact]
    public void ASilentRowStaysSilentWhateverTheSeparator()
    {
        var layout = MessageLayout();
        foreach (var f in layout) f.Enabled = false;

        foreach (var s in Enum.GetValues<RowFieldSeparator>())
            Assert.Equal(string.Empty, RowSpeechBuilder.Build(RowKind.Message, Message(), layout, separator: s));
    }

    // ── config.ini spelling ───────────────────────────────────────────────────

    [Theory]
    [InlineData("comma", RowFieldSeparator.Comma)]
    [InlineData(" Space ", RowFieldSeparator.Space)]
    [InlineData("period", RowFieldSeparator.Period)]
    [InlineData("semicolon", RowFieldSeparator.Period)]
    [InlineData("", RowFieldSeparator.Period)]
    [InlineData(null, RowFieldSeparator.Period)]
    public void UnknownConfigValuesReadAsThePeriod(string? value, RowFieldSeparator expected)
    {
        // A typo in config.ini must never change how rows sound.
        Assert.Equal(expected, RowFieldSeparators.FromConfigValue(value));
    }

    [Fact]
    public void EveryChoiceRoundTripsThroughItsConfigSpelling()
    {
        foreach (var s in Enum.GetValues<RowFieldSeparator>())
            Assert.Equal(s, RowFieldSeparators.FromConfigValue(RowFieldSeparators.ToConfigValue(s)));
    }

    // ── the bound path ────────────────────────────────────────────────────────

    [Fact]
    public void TheConverterUsesTheSettingsSeparator()
    {
        var settings = new RowSpeechSettings(RowFieldCatalog.DefaultLayouts(), false, RowFieldSeparator.Comma);
        var values   = Message("Lunch tomorrow?").Append(settings).ToArray();

        var name = (string)new RowSpeechConverter(RowKind.Message).Convert(
            values!, typeof(string), parameter: null!, CultureInfo.InvariantCulture);

        Assert.Equal("unread, Microsoft 365, Upcoming changes, Lunch tomorrow?, 10/13/2026", name);
    }

    [Fact]
    public void DefaultSettingsKeepThePeriod()
    {
        Assert.Equal(RowFieldSeparator.Period, RowSpeechSettings.Default.Separator);
        Assert.Equal(RowFieldSeparator.Period, new ConfigModel().MessageListFieldSeparator);
    }

    [Fact]
    public void TheMainWindowReadsTheSeparatorFromConfig()
    {
        // The line that delivers the choice to the real list: every other test here would still
        // pass if ReloadRowSpeech stopped passing the separator on.
        var config  = new StubConfigService();
        var layouts = new StubRowLayoutService();
        var vm = new MainViewModel(new StubImapMailService(), new StubAccountService(),
            new StubCredentialService(), new StubLocalStoreService(), new StubOAuthService(),
            new StubSyncService(), config, new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService(), rowLayoutService: layouts);
        Assert.Equal(RowFieldSeparator.Period, vm.RowSpeech.Separator);

        // What the fields window does when the choice changes: save config, then save layouts.
        var cfg = config.Load();
        cfg.MessageListFieldSeparator = RowFieldSeparator.Comma;
        config.Save(cfg);
        layouts.Save(layouts.Load());

        Assert.Equal(RowFieldSeparator.Comma, vm.RowSpeech.Separator);
    }

    // ── the chooser ───────────────────────────────────────────────────────────

    [Fact]
    public void TheChooserOffersPeriodCommaSpace_PeriodFirst()
    {
        var vm = new RowFieldsViewModel(new StubRowLayoutService(), new StubConfigService());

        Assert.Equal(["Period", "Comma", "Space"], vm.Separators.Select(s => s.Name));
        Assert.Equal(RowFieldSeparator.Period, vm.SelectedSeparator.Value);
    }

    [Fact]
    public void ChoosingASeparator_SavesIt_UpdatesThePreview_AndReSpeaksRows()
    {
        var layouts = new StubRowLayoutService();
        var cfg     = new StubConfigService();
        var vm      = new RowFieldsViewModel(layouts, cfg);
        var changed = 0;
        layouts.LayoutsChanged += (_, _) => changed++;

        vm.SelectedSeparator = vm.Separators.Single(s => s.Value == RowFieldSeparator.Space);

        Assert.Equal(RowFieldSeparator.Space, cfg.Load().MessageListFieldSeparator);
        Assert.DoesNotContain(". ", vm.Preview, StringComparison.Ordinal);
        Assert.False(vm.Preview.EndsWith('.'), vm.Preview);
        // The main window re-speaks open rows on LayoutsChanged, so the choice must raise it.
        Assert.True(changed > 0);
    }

    [Fact]
    public void TheChooserOpensOnTheSavedChoice()
    {
        var cfg = new StubConfigService();
        var c   = cfg.Load();
        c.MessageListFieldSeparator = RowFieldSeparator.Comma;
        cfg.Save(c);

        var vm = new RowFieldsViewModel(new StubRowLayoutService(), cfg);

        Assert.Equal(RowFieldSeparator.Comma, vm.SelectedSeparator.Value);
        Assert.Contains(", ", vm.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public void ResetToDefaultsLeavesTheSeparatorAlone()
    {
        // Reset restores one row type's fields. The separator, like Speak field labels, applies to
        // every row type, so resetting one type's fields must not quietly undo it.
        var vm = new RowFieldsViewModel(new StubRowLayoutService(), new StubConfigService());
        vm.SelectedSeparator = vm.Separators.Single(s => s.Value == RowFieldSeparator.Comma);

        vm.ResetDefaultsCommand.Execute(null);

        Assert.Equal(RowFieldSeparator.Comma, vm.SelectedSeparator.Value);
    }

    // ── the window ────────────────────────────────────────────────────────────

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    [StaFact]
    public void TheWindowsSeparatorListIsNamedAndItsItemsReadTheirNames()
    {
        var vm = new RowFieldsViewModel(new StubRowLayoutService(), new StubConfigService());
        var window = new RowFieldsWindow(vm)
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
        };
        window.Show();
        try
        {
            window.UpdateLayout();
            DrainDispatcher();

            var combo = window.FindName("SeparatorList") as ComboBox;
            Assert.NotNull(combo);

            var peer = UIElementAutomationPeer.CreatePeerForElement(combo!);
            Assert.Equal("Between fields", peer.GetName());
            Assert.Same(vm.SelectedSeparator, combo!.SelectedItem);

            // A screen reader reads each item's name from its peer, which comes from ToString().
            combo.IsDropDownOpen = true;
            window.UpdateLayout();
            DrainDispatcher();
            var itemNames = (peer.GetChildren() ?? [])
                .Select(c => c.GetName())
                .Where(n => !string.IsNullOrEmpty(n))
                .ToArray();
            combo.IsDropDownOpen = false;
            Assert.Equal(["Period", "Comma", "Space"], itemNames);

            // Choosing in the control reaches the view model.
            combo.SelectedIndex = 2;
            Assert.Equal(RowFieldSeparator.Space, vm.SelectedSeparator.Value);

            // Escape with this list open belongs to the list, not the window. The window's Escape
            // handler tunnels, so it sees the key first and must stand aside (review of #802).
            combo.IsDropDownOpen = true;
            Assert.True(window.IsAnyDropDownOpen);
            var escape = new System.Windows.Input.KeyEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0,
                System.Windows.Input.Key.Escape)
            { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
            window.RaiseEvent(escape);
            DrainDispatcher();
            Assert.True(window.IsVisible, "Escape on the open list closed the whole window");

            combo.IsDropDownOpen = false;
            Assert.False(window.IsAnyDropDownOpen);
        }
        finally
        {
            window.Close();
            System.Windows.Input.Keyboard.ClearFocus();
            DrainDispatcher();
        }
    }
}
