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
    public void ForNewFromTemplate_TicksNothingItHasNothingToMatchOn()
    {
        // The sender and subject are covered above. What this adds: everything the template says
        // nothing about arrives unticked, rather than ticked over an empty box.
        var vm = ServerRuleEditorViewModel.ForNewFromTemplate(Template());

        Assert.False(vm.UseFromAddresses);
        Assert.False(vm.UseSentToAddresses);
        Assert.False(vm.UseBodyOrSubjectContains);
        Assert.False(vm.UseBodyContains);
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
        // No separate address on this row yet, so the sender is a substring match on From, not an address
        // list: a display name can hold a comma, which an address field reads as a separator (#682).
        Assert.True(template!.UseSenderCondition);
        Assert.Equal("boss@work.com", template.SenderContains);
        // Carried so it can be switched on in the editor, but not ANDed into the rule by default.
        Assert.Equal("Weekly Report", template.SubjectContains);
        Assert.False(template.UseSubjectCondition);
    }

    private static MailRule TemplateFor(string from, string fromEmail) => MainViewModel.RuleTemplateFrom(
        new MailMessageSummary { MessageId = "1", FolderName = "INBOX", From = from, FromEmail = fromEmail, Subject = "Receipt" });

    [Fact]
    public void CreateRuleFromMessage_PutsTheAddressInFromAddresses_AndTheNameInSenderContains()
    {
        // The address is the condition switched on: the display name alone would also catch anyone else using it,
        // and since #801 a Microsoft row's From is the name alone. The name is filled in beside it, switched off,
        // one keystroke from a rule that wants both. The rule's name keeps the name the user knows.
        var template = TemplateFor("PayPal", "service@paypal.example");

        Assert.Equal(["service@paypal.example"], template.FromValues());
        Assert.Equal("PayPal", template.SenderContains);
        Assert.False(template.UseSenderCondition);
        Assert.Equal("Rule for PayPal", template.Name);
    }

    [Fact]
    public void CreateRuleFromMessage_ForASenderWithNoName_PutsTheAddressInBoth()
    {
        var template = TemplateFor("news@company.example", "news@company.example");

        Assert.Equal(["news@company.example"], template.FromValues());
        Assert.Equal("news@company.example", template.SenderContains);
        Assert.False(template.UseSenderCondition);
    }

    [Fact]
    public void CreateRuleFromMessage_ListsEveryAddressOfAMessageFromSeveral()
    {
        // A From header naming two mailboxes is rare, but it is shown, not chosen for the user: the address list
        // means any one of them, and the box says so.
        var template = TemplateFor("Ann and Bob", "ann@x.example, bob@y.example");

        Assert.Equal(["ann@x.example", "bob@y.example"], template.FromValues());
        Assert.False(template.UseSenderCondition);
    }

    [Fact]
    public void CreateRuleFromMessage_FallsBackToTheName_WhenTheRowHasNoAddressYet()
    {
        // A row cached before FromEmail and not yet backfilled: exactly what this proposed before.
        var template = TemplateFor("PayPal", "");

        Assert.Equal("PayPal", template.SenderContains);
        Assert.True(template.UseSenderCondition);
        Assert.Null(template.FromAddresses);
    }

    [Fact]
    public void TheEditor_OpensWithTheAddressOn_AndTheNameFilledInButOff()
    {
        var vm = ServerRuleEditorViewModel.ForNewFromTemplate(TemplateFor("PayPal", "service@paypal.example"));

        Assert.Equal("service@paypal.example", vm.FromAddresses);
        Assert.True(vm.UseFromAddresses);
        Assert.Equal("PayPal", vm.SenderContains);
        Assert.False(vm.UseSenderContains);
        Assert.False(vm.IsAdvancedExpanded);   // both sender conditions are in the main section
    }

    [Fact]
    public void TheEditor_WithNoAddressYet_OpensWithTheNameOn()
    {
        var vm = ServerRuleEditorViewModel.ForNewFromTemplate(TemplateFor("PayPal", ""));

        Assert.Equal(string.Empty, vm.FromAddresses);
        Assert.False(vm.UseFromAddresses);
        Assert.Equal("PayPal", vm.SenderContains);
        Assert.True(vm.UseSenderContains);
    }

    [Fact]
    public void SavingIt_MatchesOnTheAddressAlone_AndKeepsTheNameForLater()
    {
        // What the editor saves is what the user saw: the address required, the name kept but not tested, so the
        // same sender writing under another name is still caught.
        var vm = ServerRuleEditorViewModel.ForNewFromTemplate(TemplateFor("PayPal", "service@paypal.example"));
        vm.MarkAsRead = true;

        var rule = vm.ToClientRule(Guid.NewGuid());

        Assert.Equal(["service@paypal.example"], rule.FromValues());
        Assert.Equal("PayPal", rule.SenderContains);
        Assert.False(rule.UseSenderCondition);
        Assert.True(QuickMail.Services.RuleService.SenderMatches(
            new MailMessageSummary { From = "PayPal Billing", FromEmail = "service@paypal.example" }, rule.FromValues()[0]));
    }

    [Fact]
    public void SwitchingTheNameOnToo_RequiresBoth()
    {
        var vm = ServerRuleEditorViewModel.ForNewFromTemplate(TemplateFor("PayPal", "service@paypal.example"));
        vm.MarkAsRead = true;
        vm.UseSenderContains = true;

        var rule = vm.ToClientRule(Guid.NewGuid());

        Assert.Equal(["service@paypal.example"], rule.FromValues());
        Assert.True(rule.UseSenderCondition);
        Assert.Equal("PayPal", rule.SenderContains);
    }

    // ── Switching between the two sender conditions keeps the text (review of #806) ──

    private static ServerRuleEditorViewModel Reopen(ServerRuleEditorViewModel vm)
        => ServerRuleEditorViewModel.ForEditClient(vm.ToClientRule(Guid.NewGuid()));

    [Fact]
    public void TurningTheAddressesOff_ForTheName_KeepsTheAddressesForLater()
    {
        // Manual testing found this: saved with only Sender contains on, the address list came back empty.
        var vm = ServerRuleEditorViewModel.ForNewFromTemplate(TemplateFor("Dana", "dana@x.example"));
        vm.MarkAsRead = true;
        vm.UseSenderContains = true;
        vm.UseFromAddresses = false;

        var reopened = Reopen(vm);

        Assert.True(reopened.UseSenderContains);
        Assert.Equal("Dana", reopened.SenderContains);
        Assert.False(reopened.UseFromAddresses);
        Assert.Equal("dana@x.example", reopened.FromAddresses);
    }

    [Fact]
    public void SwitchingBackToTheAddresses_UsesThemAgain()
    {
        var vm = ServerRuleEditorViewModel.ForNewFromTemplate(TemplateFor("Dana", "dana@x.example"));
        vm.MarkAsRead = true;
        vm.UseSenderContains = true;
        vm.UseFromAddresses = false;
        var reopened = Reopen(vm);

        reopened.UseFromAddresses = true;
        reopened.UseSenderContains = false;
        var rule = reopened.ToClientRule(Guid.NewGuid());

        Assert.Equal(["dana@x.example"], rule.FromValues());
        Assert.False(rule.UseSenderCondition);
        Assert.Null(rule.FromAddressesOff);
    }

    [Fact]
    public void KeptAddresses_AreNotACondition()
    {
        // Only the editor reads them. The rule matches on the name alone, and an older QuickMail, which reads only
        // the old From field, still tests the sender's name there.
        var vm = ServerRuleEditorViewModel.ForNewFromTemplate(TemplateFor("Dana", "dana@x.example"));
        vm.MarkAsRead = true;
        vm.UseSenderContains = true;
        vm.UseFromAddresses = false;

        var rule = vm.ToClientRule(Guid.NewGuid());

        Assert.Equal(["dana@x.example"], rule.FromAddressesOff);
        Assert.Empty(rule.FromValues());
        Assert.True(rule.UseSenderCondition);
        Assert.Null(rule.FromAddresses);
        Assert.Equal("Dana", rule.FromContains);
        Assert.True(QuickMail.Services.RuleService.SenderMatches(
            new MailMessageSummary { From = "Dana", FromEmail = "dana@another.example" }, rule.SenderContains!));
    }

    [Fact]
    public void KeptAddresses_SurviveBeingWrittenToDisk()
    {
        var rule = new MailRule
        {
            Name = "Dana", UseFromCondition = true, FromContains = "Dana",
            UseSenderCondition = true, SenderContains = "Dana", FromAddressesOff = ["dana@x.example"],
            Action = RuleAction.MarkAsRead,
        };

        var json = System.Text.Json.JsonSerializer.Serialize(rule);
        var back = System.Text.Json.JsonSerializer.Deserialize<MailRule>(json)!;

        Assert.Equal(["dana@x.example"], back.FromAddressesOff);
        Assert.Equal("dana@x.example", ServerRuleEditorViewModel.ForEditClient(back).FromAddresses);
        // And a rule that never had any writes nothing new.
        Assert.DoesNotContain("FromAddressesOff",
            System.Text.Json.JsonSerializer.Serialize(new MailRule { Name = "Plain", FromContains = "a@x.example" }),
            StringComparison.Ordinal);
    }
}
