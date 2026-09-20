using System;
using System.Linq;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Per-condition on/off switches in the rule editor (issue #665).
///
/// Reported against the editor reached by Ctrl+Shift+T: it prefilled the message's From *and*
/// Subject and offered no way to say which of them the rule was meant to use, so the saved rule
/// used both — matching, in practice, only the thread it was made from.
///
/// Two things are load-bearing here and are easy to break separately:
/// <list type="bullet">
/// <item>A switched-off condition must be invisible to <b>every</b> consumer — the saved model, the
/// client-rule mapping, and the server/client classification — not merely greyed out in the UI.
/// The VM routes all of them through its private Effective* accessors; a new consumer that reads
/// the raw text property instead is the regression these tests catch.</item>
/// <item>Switching a condition off must <b>keep the text</b>. That is the whole point of a switch
/// rather than a Clear button: a prefilled value stays one keystroke from being used again.</item>
/// </list>
/// </summary>
public class RuleConditionSwitchTests
{
    private static MailRule Template(string from = "boss@work.com", string? subject = "Weekly Report") => new()
    {
        Name = $"Rule for {from}",
        SenderContains = from,
        UseSenderCondition = true,
        SubjectContains = subject,
        UseSubjectCondition = false,   // what MainViewModel.CreateRuleFromMessage now builds
    };

    // ── Ctrl+Shift+T prefill ────────────────────────────────────────────────

    [Fact]
    public void ForNewFromTemplate_CarriesSubjectText_ButLeavesTheConditionOff()
    {
        var vm = ServerRuleEditorViewModel.ForNewFromTemplate(Template());

        Assert.True(vm.UseSenderContains);
        Assert.Equal("boss@work.com", vm.SenderContains);

        // The text is present so the user can switch it on; the condition is not part of the rule.
        Assert.Equal("Weekly Report", vm.SubjectContains);
        Assert.False(vm.UseSubjectContains);
    }

    [Fact]
    public void ForNewFromTemplate_SavedRuleMatchesTheSenderOnly()
    {
        var vm = ServerRuleEditorViewModel.ForNewFromTemplate(Template());

        var model = vm.ToModel();

        Assert.Equal("boss@work.com", model.SenderContains);
        Assert.Empty(model.FromAddresses);
        Assert.Null(model.SubjectContains);
    }

    [Fact]
    public void SwitchingSubjectOn_AddsItBackWithoutRetyping()
    {
        var vm = ServerRuleEditorViewModel.ForNewFromTemplate(Template());

        vm.UseSubjectContains = true;

        Assert.Equal("Weekly Report", vm.ToModel().SubjectContains);
    }

    [Fact]
    public void SwitchingTheSenderOff_DropsItFromTheSavedRule_ButKeepsTheText()
    {
        var vm = ServerRuleEditorViewModel.ForNewFromTemplate(Template());

        vm.UseSenderContains = false;

        Assert.Null(vm.ToModel().SenderContains);
        Assert.Equal("boss@work.com", vm.SenderContains);   // still there to switch back on
    }

    [Fact]
    public void ForNewFromTemplate_EmptySubject_LeavesNothingToSwitchOn()
    {
        var vm = ServerRuleEditorViewModel.ForNewFromTemplate(Template(subject: null));

        Assert.Equal(string.Empty, vm.SubjectContains);
        Assert.Null(vm.ToModel().SubjectContains);
    }

    // ── A hand-made new rule is unchanged ───────────────────────────────────

    [Fact]
    public void ForNew_StartsWithEveryConditionSwitchedOff()
    {
        // Only Enabled is ticked on a new rule; a condition is one the user asked for. They started ON,
        // on the reasoning that an empty field is no condition either way — true of what gets SAVED, but
        // not of what the form says. Arrowing the Advanced section gave three ticked boxes over empty
        // fields, reading as a rule that tests things it does not.
        var vm = ServerRuleEditorViewModel.ForNew();

        Assert.False(vm.UseFromAddresses);
        Assert.False(vm.UseSubjectContains);
        Assert.False(vm.UseSenderContains);
        Assert.False(vm.UseSentToAddresses);
        Assert.False(vm.UseBodyOrSubjectContains);
        Assert.False(vm.UseBodyContains);

        Assert.True(vm.IsEnabled);   // the one thing that is ticked

        var model = vm.ToModel();
        Assert.Null(model.SubjectContains);
        Assert.Empty(model.FromAddresses);
    }

    // ── Loading an existing rule ────────────────────────────────────────────

    [Fact]
    public void ForNewFromTemplate_TicksOnlyWhatItHasSomethingToMatchOn()
    {
        // Create Rule from Message: the sender is ticked because it carries the message's sender; the
        // subject is carried but clear (see above — a rule matching this sender AND this exact subject
        // matches the one thread it was made from); and nothing else is ticked, because nothing else
        // has anything in it.
        var vm = ServerRuleEditorViewModel.ForNewFromTemplate(Template());

        Assert.True(vm.UseSenderContains);
        Assert.Equal("boss@work.com", vm.SenderContains);

        Assert.False(vm.UseSubjectContains);
        Assert.Equal("Weekly Report", vm.SubjectContains);

        Assert.False(vm.UseFromAddresses);
        Assert.False(vm.UseSentToAddresses);
        Assert.False(vm.UseBodyOrSubjectContains);
        Assert.False(vm.UseBodyContains);
    }

    [Fact]
    public void ATemplateFlagOverAnEmptyField_TicksNothing()
    {
        // A flag says the condition was wanted; the text is what it would match. Without both, ticking
        // it offers a condition that tests nothing.
        var vm = ServerRuleEditorViewModel.ForNewFromTemplate(new MailRule
        {
            Name = "Rule for nobody",
            SenderContains = "   ",
            UseSenderCondition = true,
        });

        Assert.False(vm.UseSenderContains);
    }

    [Fact]
    public void ForEdit_SwitchesOnOnlyTheConditionsTheRuleActuallyUses()
    {
        var vm = ServerRuleEditorViewModel.ForEdit(new ServerRuleModel
        {
            Id = "r1",
            DisplayName = "Newsletters",
            SubjectContains = "Digest",
            MarkAsRead = true,
        });

        Assert.True(vm.UseSubjectContains);
        Assert.False(vm.UseFromAddresses);      // empty field, so the box reads as unused
        Assert.False(vm.UseBodyContains);
        Assert.False(vm.UseSenderContains);
    }

    [Fact]
    public void ForEdit_RoundTripsUnchanged()
    {
        var original = new ServerRuleModel
        {
            Id = "r1",
            DisplayName = "Newsletters",
            FromAddresses = ["a@x.com", "b@x.com"],
            SubjectContains = "Digest",
            MarkAsRead = true,
        };

        var model = ServerRuleEditorViewModel.ForEdit(original).ToModel();

        Assert.Equal(["a@x.com", "b@x.com"], model.FromAddresses);
        Assert.Equal("Digest", model.SubjectContains);
    }

    [Fact]
    public void ForEditClient_HonoursTheClientRulesConditionFlags()
    {
        var vm = ServerRuleEditorViewModel.ForEditClient(new MailRule
        {
            Name = "Newsletters",
            FromContains = "news@x.com",
            SubjectContains = "Digest",
            UseSubjectCondition = false,      // switched off in the client Rules Manager
            Action = RuleAction.MarkAsRead,
        });

        Assert.True(vm.UseFromAddresses);
        Assert.False(vm.UseSubjectContains);
        Assert.Equal("Digest", vm.SubjectContains);   // text preserved, condition off
        Assert.Null(vm.ToModel().SubjectContains);
    }

    [Fact]
    public void ToClientRule_SwitchedOffConditionIsSavedInert_ButKeepsItsText()
    {
        var vm = ServerRuleEditorViewModel.ForNewFromTemplate(Template());
        vm.MarkAsRead = true;

        var rule = vm.ToClientRule(Guid.NewGuid());

        Assert.True(rule.UseFromCondition);
        Assert.Equal("boss@work.com", rule.FromContains);

        // The flag is what makes a condition live (RuleService requires flag AND text), so the rule
        // matches on the sender alone — but the text is kept, so reopening the rule offers it back
        // instead of an empty box. This is what the standalone Rules Manager has always stored.
        Assert.False(rule.UseSubjectCondition);
        Assert.Equal("Weekly Report", rule.SubjectContains);
    }

    [Fact]
    public void ToClientRule_CarriedTextNeverDisplacesAConditionThatIsInUse()
    {
        // Sender-contains and From-addresses share one slot in the client model. With Sender switched
        // off and From switched on, the saved rule must match the From address — carrying the dead
        // Sender text into that slot would silently change what the rule matches.
        var vm = ServerRuleEditorViewModel.ForNew();
        vm.UseSenderContains = true;
        vm.SenderContains = "accounts";
        vm.UseSenderContains = false;
        vm.UseFromAddresses = true;
        vm.FromAddresses = "billing@x.com";
        vm.MarkAsRead = true;

        var rule = vm.ToClientRule(Guid.NewGuid());

        Assert.True(rule.UseFromCondition);
        Assert.Equal("billing@x.com", rule.FromContains);
        // And the rule really tests it. The dead Sender text is kept in the rule's own sender field, so
        // without the address list beside it the old field would read as that sender's mirror and the
        // From condition would vanish — a Mark as read rule matching every message.
        Assert.False(rule.FromContainsMirrorsSender);
        Assert.Equal(["billing@x.com"], rule.FromValues());
    }

    [Fact]
    public void PopulatedButSwitchedOffAdvancedField_IsNotHiddenBehindTheCollapsedSection()
    {
        // "Editing never hides a populated field": a client rule whose Body condition was switched
        // off in the standalone Rules Manager still carries its text, and the Advanced section has to
        // open so the user can see it — otherwise the editor holds text they cannot see.
        var vm = ServerRuleEditorViewModel.ForEditClient(new MailRule
        {
            Name = "Invoices",
            FromContains = "billing@x.com",
            BodyContains = "invoice",
            UseBodyCondition = false,
            Action = RuleAction.MarkAsRead,
        });

        Assert.Equal("invoice", vm.BodyContains);
        Assert.False(vm.UseBodyContains);
        Assert.True(vm.IsAdvancedExpanded);
    }

    // ── Classification (spec §20.3) reads the switches too ──────────────────

    [Fact]
    public void SwitchedOffCondition_DoesNotBlockTheClientMapping()
    {
        // A client rule has one subject condition, which "subject or body" widens rather than joins
        // (#682), so while both are switched ON the rule can only run on the server. Switching one off
        // must actually remove it from the classifier's view — if the classifier read the raw text it
        // would keep insisting the rule is server-only.
        var vm = ServerRuleEditorViewModel.ForNew();
        vm.UseSubjectContains = true;
        vm.SubjectContains = "Digest";
        vm.UseBodyOrSubjectContains = true;
        vm.BodyOrSubjectContains = "invoice";
        vm.MarkAsRead = true;

        Assert.False(vm.IsClientRepresentable);

        vm.UseBodyOrSubjectContains = false;

        Assert.True(vm.IsClientRepresentable);
    }

    [Fact]
    public void SwitchedOffFromAddresses_AreNotAConditionAMoveRuleCanRestOn()
    {
        // Move needs a condition, or the rule empties the Inbox. Addresses that are switched off are
        // not one, however much text is sitting in the box.
        var vm = ServerRuleEditorViewModel.ForNew();
        vm.Name = "Filing";
        vm.UseFromAddresses = true;
        vm.FromAddresses = "a@x.com, b@x.com";
        vm.MoveToFolder = true;
        vm.MoveToFolderId = "Archive";

        Assert.True(vm.Validate());

        vm.UseFromAddresses = false;

        Assert.False(vm.Validate());
        Assert.Contains(ServerRuleEditorViewModel.NoConditionError, vm.ActionsError, StringComparison.Ordinal);
    }

    // ── The command that starts it all ──────────────────────────────────────

    [Fact]
    public void CreateRuleFromMessage_BuildsATemplateWhoseSubjectConditionIsOff()
    {
        var config = new StubConfigService();
        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(), config,
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(), new StubSmtpService());

        MailRule? template = null;
        vm.CreateRuleFromMessageRequested += (_, t) => template = t;

        vm.SelectedMessage = new MailMessageSummary
        {
            MessageId = "1",
            FolderName = "INBOX",
            From = "boss@work.com",
            Subject = "Weekly Report",
        };
        vm.CreateRuleFromMessageCommand.Execute(null);

        Assert.NotNull(template);
        // The sender is a substring match on From, not an address list: a display name can hold a
        // comma, which an address field reads as a separator (#682).
        Assert.True(template!.UseSenderCondition);
        Assert.Equal("boss@work.com", template.SenderContains);
        // Carried so it can be switched on in the editor, but not ANDed into the rule by default.
        Assert.Equal("Weekly Report", template.SubjectContains);
        Assert.False(template.UseSubjectCondition);
    }
}
