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
        FromContains = from,
        SubjectContains = subject,
        UseSubjectCondition = false,   // what MainViewModel.CreateRuleFromMessage now builds
    };

    // ── Ctrl+Shift+T prefill ────────────────────────────────────────────────

    [Fact]
    public void ForNewFromTemplate_CarriesSubjectText_ButLeavesTheConditionOff()
    {
        var vm = ServerRuleEditorViewModel.ForNewFromTemplate(Template());

        Assert.True(vm.UseFromAddresses);
        Assert.Equal("boss@work.com", vm.FromAddresses);

        // The text is present so the user can switch it on; the condition is not part of the rule.
        Assert.Equal("Weekly Report", vm.SubjectContains);
        Assert.False(vm.UseSubjectContains);
    }

    [Fact]
    public void ForNewFromTemplate_SavedRuleMatchesTheSenderOnly()
    {
        var vm = ServerRuleEditorViewModel.ForNewFromTemplate(Template());

        var model = vm.ToModel();

        Assert.Equal(["boss@work.com"], model.FromAddresses);
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
    public void SwitchingFromOff_DropsItFromTheSavedRule_ButKeepsTheText()
    {
        var vm = ServerRuleEditorViewModel.ForNewFromTemplate(Template());

        vm.UseFromAddresses = false;

        Assert.Empty(vm.ToModel().FromAddresses);
        Assert.Equal("boss@work.com", vm.FromAddresses);   // still there to switch back on
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
    public void ForNew_StartsWithEveryConditionSwitchedOn()
    {
        // An empty field was, and still is, no condition — so defaulting the switches on keeps the
        // "just type in the boxes you want" flow working exactly as it did before the switches existed.
        var vm = ServerRuleEditorViewModel.ForNew();

        Assert.True(vm.UseFromAddresses);
        Assert.True(vm.UseSubjectContains);
        Assert.True(vm.UseSenderContains);
        Assert.True(vm.UseSentToAddresses);
        Assert.True(vm.UseBodyOrSubjectContains);
        Assert.True(vm.UseBodyContains);

        var model = vm.ToModel();
        Assert.Null(model.SubjectContains);
        Assert.Empty(model.FromAddresses);
    }

    // ── Loading an existing rule ────────────────────────────────────────────

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
    public void ToClientRule_SwitchedOffConditionIsNotCarriedOver()
    {
        var vm = ServerRuleEditorViewModel.ForNewFromTemplate(Template());
        vm.MarkAsRead = true;

        var rule = vm.ToClientRule(Guid.NewGuid());

        Assert.True(rule.UseFromCondition);
        Assert.Equal("boss@work.com", rule.FromContains);
        Assert.False(rule.UseSubjectCondition);
        Assert.Null(rule.SubjectContains);
    }

    // ── Classification (spec §20.3) reads the switches too ──────────────────

    [Fact]
    public void SwitchedOffServerOnlyCondition_DoesNotBlockTheClientMapping()
    {
        // Subject-or-body has no client equivalent, so while it is switched ON the rule can only run
        // on the server. Switching it off must actually remove it from the classifier's view — if the
        // classifier read the raw text it would keep insisting the rule is server-only.
        var vm = ServerRuleEditorViewModel.ForNew();
        vm.SubjectContains = "Digest";
        vm.BodyOrSubjectContains = "invoice";
        vm.MarkAsRead = true;

        Assert.False(vm.IsClientRepresentable);

        vm.UseBodyOrSubjectContains = false;

        Assert.True(vm.IsClientRepresentable);
    }

    [Fact]
    public void SwitchedOffFromAddresses_DoesNotCountAsMultipleFromAddresses()
    {
        var vm = ServerRuleEditorViewModel.ForNew();
        vm.FromAddresses = "a@x.com, b@x.com";     // more than one From: server-only
        vm.SubjectContains = "Digest";
        vm.MarkAsRead = true;

        Assert.False(vm.IsClientRepresentable);

        vm.UseFromAddresses = false;

        Assert.True(vm.IsClientRepresentable);
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
        Assert.True(template!.UseFromCondition);
        Assert.Equal("boss@work.com", template.FromContains);
        // Carried so it can be switched on in the editor, but not ANDed into the rule by default.
        Assert.Equal("Weekly Report", template.SubjectContains);
        Assert.False(template.UseSubjectCondition);
    }
}
