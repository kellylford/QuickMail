using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Automation;
using QuickMail.Models;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The options only a server-side rule can carry are turned OFF where the rule cannot become one
/// (issue #682, the last of its four parts).
/// <para>
/// The report: on an account that can only have client-side rules — any IMAP account, a personal
/// Outlook.com account, a work account added over Standard IMAP/SMTP — the editor offered every
/// server-side option and said nothing, and the user found out only on pressing Save, by which point
/// they had built the rule. Turning them off was chosen over labelling them: six labels all ending
/// "(server rules only)" is five syllables repeated six times in one section.
/// </para>
/// <para>
/// A disabled control leaves the Tab order, which is the real cost of that choice — the options do not
/// read as unavailable, they are simply not there. <see cref="ServerRuleEditorViewModel.ServerOnlyOptionsDisabledReason"/>
/// is what answers "why", so it is pinned here too.
/// </para>
/// </summary>
[Collection("WpfTests")]
public class ServerOnlyOptionsDisabledTests
{
    private static ServerRuleEditorWindow NewWindow(ServerRuleEditorViewModel vm)
    {
        WpfTestHost.EnsureStyles("AccessibleStyles", "ThemedControls");
        return new(vm, new List<AccountModel>(), new Dictionary<Guid, List<MailFolderModel>>());
    }

    private static ServerRuleModel ServerRule() => new() { Id = "r1", DisplayName = "A server rule" };

    private static MailRule ClientRule() => new()
    {
        Name = "A client rule",
        UseFromCondition = true,
        FromContains = "someone@example.com",
        Action = RuleAction.MarkAsRead,
    };

    // ── When the options are available ──────────────────────────────────────

    [Fact]
    public void OnAnAccountWithClientRulesOnly_TheyAreTurnedOff()
    {
        var vm = ServerRuleEditorViewModel.ForNew();
        vm.AccountSupportsServerRules = false;

        Assert.False(vm.CanUseServerOnlyOptions);
        Assert.Contains("only has client-side rules", vm.ServerOnlyOptionsDisabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public void OnAMicrosoft365Account_ANewRuleStillOffersThem()
    {
        // Ticking one there is not a mistake: it is what makes the rule a server rule.
        var vm = ServerRuleEditorViewModel.ForNew();
        vm.AccountSupportsServerRules = true;

        Assert.True(vm.CanUseServerOnlyOptions);
        Assert.Equal(string.Empty, vm.ServerOnlyOptionsDisabledReason);
    }

    [Fact]
    public void EditingAClientRule_TurnsThemOff_EvenWhereTheAccountHasServerRules()
    {
        // Editing never changes a rule's kind (spec §20.6), so a rule that runs in QuickMail cannot take
        // one of these however capable its account is — and the reason it gives says so, rather than
        // blaming the account.
        var vm = ServerRuleEditorViewModel.ForEditClient(ClientRule());
        vm.AccountSupportsServerRules = true;

        Assert.False(vm.CanUseServerOnlyOptions);
        Assert.Contains("runs in QuickMail", vm.ServerOnlyOptionsDisabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public void EditingAServerRule_LeavesThemOn()
    {
        var vm = ServerRuleEditorViewModel.ForEdit(ServerRule());
        vm.AccountSupportsServerRules = true;

        Assert.True(vm.CanUseServerOnlyOptions);
    }

    [Fact]
    public void EditingAServerRule_LeavesThemOn_EvenIfToldTheAccountCannot()
    {
        // The direction that costs function rather than wording. A server rule already carries these,
        // and editing never changes its kind, so the account's capability cannot come into it — being
        // told otherwise would grey the very fields the rule uses and leave them unchangeable. This is
        // what makes "fails open: an option already set stays clearable" true rather than aspirational.
        var vm = ServerRuleEditorViewModel.ForEdit(new ServerRuleModel
        {
            Id = "r1",
            DisplayName = "Forward invoices",
            ForwardTo = ["ap@contoso.com"],
            StopProcessingRules = true,
        });
        vm.AccountSupportsServerRules = false;

        Assert.True(vm.CanUseServerOnlyOptions);
        Assert.Equal(string.Empty, vm.ServerOnlyOptionsDisabledReason);
    }

    [Fact]
    public void AnEditorNobodyToldAboutTheAccount_FailsOpen()
    {
        // The default has to offer everything and refuse on save, as before #682. Failing the other way
        // would turn an option off with no way to clear one that is already set.
        Assert.True(ServerRuleEditorViewModel.ForNew().CanUseServerOnlyOptions);
    }

    [Fact]
    public void MarkAsUnreadIsTheMirrorImage_AndIsNotCaughtUpInThis()
    {
        // The one option only a CLIENT rule can carry (#684). On a client-only account it must stay
        // available — it is the reason that account's rules work at all.
        var vm = ServerRuleEditorViewModel.ForNew();
        vm.AccountSupportsServerRules = false;

        Assert.False(vm.CanUseServerOnlyOptions);
        Assert.True(vm.CanMarkAsUnread);
    }

    // ── Every server-only control is actually wired to it ───────────────────

    /// <summary>
    /// Which options are server-only, DERIVED rather than listed: each of the editor's bound values is
    /// set away from its default on an otherwise client-representable form, and the ones that make
    /// <see cref="ServerRuleEditorViewModel.IsClientRepresentable"/> false are exactly the ones a
    /// client-side rule cannot carry.
    /// <para>
    /// A hand-kept list in the test cannot catch the failure it exists for: somebody adding the seventh
    /// server-only option by copying a control that has no gate omits it from the list too, and the test
    /// passes. Deriving it means a new option is found the moment the model refuses it.
    /// </para>
    /// </summary>
    private static List<string> ServerOnlyBindingPaths(IEnumerable<string> boundPaths)
    {
        var found = new List<string>();
        foreach (var path in boundPaths.Distinct())
        {
            var vm = ServerRuleEditorViewModel.ForNew();
            vm.Name = "Rule";
            vm.MarkAsRead = true;                       // one action a client rule can do
            if (!vm.IsClientRepresentable) continue;    // baseline must start representable

            var property = typeof(ServerRuleEditorViewModel).GetProperty(path);
            Assert.True(property is not null && property.CanWrite,
                $"'{path}' is bound in the editor but cannot be set here, so this guard would skip it silently. Give it a setter, or exclude it deliberately.");
            property!.SetValue(vm, NonDefaultFor(property.PropertyType));
            if (!vm.IsClientRepresentable) found.Add(path);
        }
        return found;
    }

    /// <summary>
    /// A value that is not the property's default, so setting it tells us whether the option it carries
    /// is one a client rule can hold. An unknown type fails loudly rather than being skipped: a guard
    /// that quietly ignores what it doesn't recognise is the failure this test exists to prevent.
    /// </summary>
    private static object NonDefaultFor(Type type)
    {
        if (type == typeof(bool)) return true;
        if (type == typeof(string)) return "someone@example.com";
        if (type == typeof(ImportanceOption))
            return ServerRuleEditorViewModel.ImportanceOptions.First(o => o.Value == "high");
        throw new InvalidOperationException(
            $"No non-default value known for {type.Name}. Add one here so the new field is actually checked.");
    }

    [StaFact]
    public void EveryOptionAClientRuleCannotCarry_IsGatedOnCanUseServerOnlyOptions()
    {
        var window = NewWindow(ServerRuleEditorViewModel.ForNew());
        try
        {
            var all = Descendants(window).ToList();
            var bound = all
                .Select(e => (Element: e, Path: ValueBindingPath(e)))
                .Where(x => x.Path is not null)
                .ToList();

            // A control that HOLDS A VALUE but whose type ValueBindingPath cannot read drops out of BOTH
            // assertions below and is checked by neither — a RadioButton is not a CheckBox, so turning
            // the importance combos into a radio group would slip straight past this guard. The three
            // families below are every WPF control that carries one; containers hold none and are not
            // the risk. Make an unreadable one a failure, not a silent gap.
            var unreadable = all
                .Where(e => e is ToggleButton or Selector or TextBoxBase && ValueBindingPath(e) is null)
                .Select(e => e.GetType().Name)
                .Distinct()
                .ToList();
            Assert.True(unreadable.Count == 0,
                $"the editor holds value-carrying controls this guard cannot read ({string.Join(", ", unreadable)}); teach ValueBindingPath about them or they are checked by nothing.");

            var serverOnly = ServerOnlyBindingPaths(bound.Select(x => x.Path!));
            Assert.NotEmpty(serverOnly);   // a derivation that finds nothing would pass vacuously

            foreach (var path in serverOnly)
            {
                foreach (var control in bound.Where(x => x.Path == path).Select(x => x.Element))
                {
                    var enabled = BindingOperations.GetBinding(control, UIElement.IsEnabledProperty);
                    Assert.True(enabled is not null,
                        $"{path} is an option a client-side rule cannot carry, but its control has no IsEnabled binding — it would stay usable on an account that cannot run it.");
                    Assert.Equal(nameof(ServerRuleEditorViewModel.CanUseServerOnlyOptions), enabled!.Path.Path);
                }
            }

            // And nothing a client rule CAN carry is caught up in the gate — Mark as unread above all,
            // which is the one option only a client rule has.
            foreach (var (element, path) in bound.Where(x => !serverOnly.Contains(x.Path!)))
            {
                var enabled = BindingOperations.GetBinding(element, UIElement.IsEnabledProperty);
                Assert.True(enabled?.Path.Path != nameof(ServerRuleEditorViewModel.CanUseServerOnlyOptions),
                    $"{path} works in a client-side rule, so turning it off on a client-only account would take away something that works.");
            }
        }
        finally
        {
            // A Window joins Application.Current.Windows at construction, not at Show(), and leaves only
            // on Close() — see CLAUDE.md. One built and dropped keeps the app alive.
            window.Close();
        }
    }

    [StaFact]
    public void TheReasonIsOnTheForm_SoTurningThemOffIsNotSilent()
    {
        var vm = ServerRuleEditorViewModel.ForNew();
        vm.AccountSupportsServerRules = false;
        var window = NewWindow(vm);
        try
        {
            var notice = Descendants(window).OfType<TextBlock>().SingleOrDefault(t =>
                BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path.Path
                    == nameof(ServerRuleEditorViewModel.ServerOnlyOptionsDisabledReason));

            Assert.True(notice is not null, "nothing on the form binds the reason, so the options would go quiet rather than unavailable.");

            // Reachable. An unfocusable TextBlock is read only in a screen reader's own review mode,
            // which is no answer to six controls leaving the Tab order — the rules window's status line
            // is a focus stop for the same reason.
            Assert.True(notice!.Focusable);
            Assert.Equal(nameof(ServerRuleEditorViewModel.HasServerOnlyOptionsDisabledReason),
                BindingOperations.GetBinding(notice, System.Windows.Input.KeyboardNavigation.IsTabStopProperty)?.Path.Path);
            Assert.Equal(nameof(ServerRuleEditorViewModel.ServerOnlyOptionsDisabledReason),
                BindingOperations.GetBinding(notice, System.Windows.Automation.AutomationProperties.NameProperty)?.Path.Path);

            // And it says WHICH options, since nobody who cannot see them greyed can work that out.
            Assert.True(vm.HasServerOnlyOptionsDisabledReason);
            var named = new[] { "Sent to me", "Sent only to me", "Importance is",
                                "Set importance to", "Forward to", "Stop processing more rules" };
            foreach (var option in named)
                Assert.Contains(option, vm.ServerOnlyOptionsDisabledReason, StringComparison.Ordinal);

            // As many as there actually are. A seventh option gated but left out of the sentence would
            // tell the user "these are turned off" and then list the wrong set.
            var bound = Descendants(window)
                .Select(ValueBindingPath).Where(p => p is not null).Select(p => p!);
            Assert.Equal(ServerOnlyBindingPaths(bound).Count, named.Length);

            // Named by the words the controls themselves announce, not by something only on screen.
            foreach (var option in named)
                Assert.Contains(option, AccessibleNames(window), StringComparison.Ordinal);
        }
        finally { window.Close(); }
    }

    /// <summary>Every AutomationProperties.Name the window sets, joined — what the controls announce.</summary>
    private static string AccessibleNames(DependencyObject root)
        => string.Join(" | ", Descendants(root)
            .Select(e => System.Windows.Automation.AutomationProperties.GetName(e))
            .Where(n => !string.IsNullOrEmpty(n)));

    /// <summary>The path a control binds its VALUE to — whichever property carries it for that control.</summary>
    private static string? ValueBindingPath(DependencyObject element) => element switch
    {
        CheckBox c => BindingOperations.GetBinding(c, ToggleButtonIsChecked)?.Path.Path,
        ComboBox c => BindingOperations.GetBinding(c, System.Windows.Controls.Primitives.Selector.SelectedItemProperty)?.Path.Path,
        TextBox t => BindingOperations.GetBinding(t, TextBox.TextProperty)?.Path.Path,
        _ => null,
    };

    private static readonly DependencyProperty ToggleButtonIsChecked =
        System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty;

    /// <summary>
    /// The logical tree, not the visual one: a Window that is never shown has no visual tree, and the
    /// logical one carries every element the XAML declares — including the Advanced expander's, whether
    /// or not it is expanded. Bindings are attached at parse time, which is all this reads.
    /// </summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
