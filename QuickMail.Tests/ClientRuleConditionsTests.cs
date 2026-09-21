using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Conditions a client-side rule gained in #682: several From addresses, several Sent-to addresses,
/// Sender contains alongside those addresses, and "subject or body contains". Before it, the one editor
/// every account now uses offered all four and only refused them on Save, so an IMAP account could build
/// a rule it was never allowed to keep.
/// <para>
/// Two properties are load-bearing and easy to break apart:
/// <list type="bullet">
/// <item>The <b>"or" lives inside one condition</b>. Several addresses in a field means any one of them
/// matches; conditions are still ANDed with each other. Widening that "or" across two conditions would
/// make a Move rule act on mail the user never described.</item>
/// <item>A rule stays readable to a <b>QuickMail from before #682</b>, which reads the single-value
/// fields alone. Those fields keep a value that matches <i>less</i> mail than the rule really covers —
/// the first of the addresses, the subject half of subject-or-body — with one documented exception
/// (Sender contains alongside addresses, where one old field cannot hold two ANDed conditions; see
/// <see cref="MailRule.SenderContains"/>). The tests below pin that direction, because the field is
/// otherwise invisible from inside this build.</item>
/// </list>
/// </para>
/// </summary>
public class ClientRuleConditionsTests
{
    private static MailMessageSummary Msg(
        string from = "alice@example.com",
        string to = "bob@example.com",
        string subject = "Test Subject",
        string preview = "Hello world")
        => new()
        {
            MessageId = "1",
            AccountId = Guid.NewGuid(),
            FolderName = "INBOX",
            From = from,
            To = to,
            Subject = subject,
            Preview = preview,
            Date = DateTimeOffset.Now,
        };

    /// <summary>Runs a rule's conditions over one message, through the real service.</summary>
    private static bool Matches(MailRule rule, MailMessageSummary msg)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var svc = new RuleService(new StubImapMailService(), new StubLocalStoreService(), dir);
            return svc.TestRule(rule, [msg]).Count == 1;
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // ── Matching ────────────────────────────────────────────────────────────

    [Fact]
    public void SeveralFromAddresses_MatchAnyOneOfThem()
    {
        var rule = new MailRule
        {
            Name = "Billing",
            UseFromCondition = true,
            FromContains = "billing@x.com",
            FromAddresses = ["billing@x.com", "invoices@y.com"],
            Action = RuleAction.MarkAsRead,
        };

        Assert.True(Matches(rule, Msg(from: "billing@x.com")));
        Assert.True(Matches(rule, Msg(from: "Invoices <invoices@y.com>")));
        Assert.False(Matches(rule, Msg(from: "someone@z.com")));
    }

    [Fact]
    public void SeveralSentToAddresses_MatchAnyOneOfThem()
    {
        var rule = new MailRule
        {
            Name = "Lists",
            UseFromCondition = false,
            UseToCondition = true,
            ToContains = "dev@list.org",
            SentToAddresses = ["dev@list.org", "ops@list.org"],
            Action = RuleAction.MarkAsRead,
        };

        Assert.True(Matches(rule, Msg(to: "ops@list.org")));
        Assert.False(Matches(rule, Msg(to: "sales@list.org")));
    }

    [Fact]
    public void SenderContains_IsAndedWithTheAddresses_NotOredIntoThem()
    {
        // The whole point of keeping the two conditions apart: a message from one of the listed
        // addresses that does not also match the sender text must NOT match.
        var rule = new MailRule
        {
            Name = "Acme billing",
            UseFromCondition = true,
            FromContains = "billing@acme.com",
            FromAddresses = ["billing@acme.com", "noreply@partner.com"],
            UseSenderCondition = true,
            SenderContains = "acme",
            Action = RuleAction.MarkAsRead,
        };

        Assert.True(Matches(rule, Msg(from: "billing@acme.com")));
        Assert.True(Matches(rule, Msg(from: "Acme partner <noreply@partner.com>")));
        Assert.False(Matches(rule, Msg(from: "noreply@partner.com")));
    }

    [Fact]
    public void SubjectOrBody_MatchesEitherPlace()
    {
        var rule = new MailRule
        {
            Name = "Invoices",
            UseFromCondition = false,
            UseSubjectCondition = true,
            SubjectContains = "invoice",
            SubjectAlsoMatchesBody = true,
            Action = RuleAction.MarkAsRead,
        };

        Assert.True(Matches(rule, Msg(subject: "Your invoice", preview: "Hello")));
        Assert.True(Matches(rule, Msg(subject: "Hello", preview: "Your invoice is attached")));
        Assert.False(Matches(rule, Msg(subject: "Hello", preview: "Nothing here")));

        // Without the flag the same rule is a subject match — which is how a QuickMail from before #682
        // reads it, and why the subject is the half that goes in the shared field.
        rule.SubjectAlsoMatchesBody = false;
        Assert.False(Matches(rule, Msg(subject: "Hello", preview: "Your invoice is attached")));
    }

    [Fact]
    public void SwitchedOffAddressList_TestsNothing()
    {
        // The flag is what makes a condition live; a list left behind with the flag clear must not
        // quietly become a condition of its own.
        var rule = new MailRule
        {
            Name = "Off",
            UseFromCondition = false,
            FromContains = "billing@x.com",
            FromAddresses = ["billing@x.com", "invoices@y.com"],
            UseToCondition = false,
            // A live condition, so the match below proves the From list was not consulted rather than
            // merely that a rule with no conditions matches everything.
            UseSubjectCondition = true,
            SubjectContains = "Digest",
            Action = RuleAction.MarkAsRead,
        };

        Assert.True(Matches(rule, Msg(from: "stranger@z.com", subject: "Weekly Digest")));
        Assert.False(Matches(rule, Msg(from: "billing@x.com", subject: "Something else")));
        Assert.Empty(rule.FromValues());
    }

    [Fact]
    public void ARuleFromAnEarlierVersion_StillMatchesOnItsSingleValues()
    {
        var rule = new MailRule
        {
            Name = "Legacy",
            UseFromCondition = true,
            FromContains = "alice@example.com",
            UseToCondition = false,
            UseSubjectCondition = false,
            Action = RuleAction.MarkAsRead,
        };

        Assert.True(Matches(rule, Msg(from: "alice@example.com")));
        Assert.False(Matches(rule, Msg(from: "bob@example.com")));
    }

    // ── What the editor saves ───────────────────────────────────────────────

    private static ServerRuleEditorViewModel Editor(string name = "Rule")
    {
        var vm = ServerRuleEditorViewModel.ForNew();
        vm.Name = name;
        vm.MarkAsRead = true;
        return vm;
    }

    [Fact]
    public void SeveralAddresses_AreSaved_AndTheOldFieldKeepsTheFirst()
    {
        var vm = Editor();
        vm.UseFromAddresses = true;
        vm.FromAddresses = "billing@x.com, invoices@y.com";
        vm.UseSentToAddresses = true;
        vm.SentToAddresses = "dev@list.org; ops@list.org";

        var rule = vm.ToClientRule(Guid.NewGuid());

        Assert.Equal(["billing@x.com", "invoices@y.com"], rule.FromAddresses);
        Assert.Equal(["dev@list.org", "ops@list.org"], rule.SentToAddresses);
        Assert.True(rule.UseFromCondition);
        Assert.True(rule.UseToCondition);

        // What a QuickMail from before #682 matches on: one of the addresses, so such a build acts on
        // less mail than this rule covers rather than on mail the rule says nothing about.
        Assert.Equal("billing@x.com", rule.FromContains);
        Assert.Equal("dev@list.org", rule.ToContains);
    }

    [Fact]
    public void OneAddress_IsSavedTheWayItAlwaysWas()
    {
        // No list where the single field says it all: an unchanged rules.json for the common rule, and
        // nothing new for an older build to ignore.
        var vm = Editor();
        vm.UseFromAddresses = true;
        vm.FromAddresses = "billing@x.com";
        vm.UseSentToAddresses = true;
        vm.SentToAddresses = "dev@list.org";

        var rule = vm.ToClientRule(Guid.NewGuid());

        Assert.Null(rule.FromAddresses);
        Assert.Null(rule.SentToAddresses);
        Assert.Equal("billing@x.com", rule.FromContains);
        Assert.Equal("dev@list.org", rule.ToContains);
    }

    [Fact]
    public void SenderContainsAlone_IsAlsoLeftInTheFieldEveryClientRuleHasUsed()
    {
        var vm = Editor();
        vm.UseSenderContains = true;
        vm.SenderContains = "acme";

        var rule = vm.ToClientRule(Guid.NewGuid());

        Assert.Equal("acme", rule.SenderContains);
        Assert.True(rule.UseSenderCondition);

        // Mirrored into the oldest field so a QuickMail from before #682, which has no sender field,
        // still tests the sender — not nothing at all.
        Assert.Equal("acme", rule.FromContains);
        Assert.True(rule.UseFromCondition);
        Assert.Null(rule.FromAddresses);

        // This build reads the mirror as the sender condition it is, so the rule tests the sender once
        // and reopens with the text in the box it was typed in.
        Assert.True(rule.FromContainsMirrorsSender);
        Assert.Empty(rule.FromValues());
        Assert.True(Matches(rule, Msg(from: "billing@acme.com")));
        Assert.False(Matches(rule, Msg(from: "billing@other.com")));

        var reopened = ServerRuleEditorViewModel.ForEditClient(rule);
        Assert.Equal("acme", reopened.SenderContains);
        Assert.True(reopened.UseSenderContains);
        Assert.Equal(string.Empty, reopened.FromAddresses);
    }

    [Fact]
    public void SenderContainsWithAddresses_KeepsBothConditions()
    {
        var vm = Editor();
        vm.UseSenderContains = true;
        vm.SenderContains = "acme";
        vm.UseFromAddresses = true;
        vm.FromAddresses = "billing@acme.com";

        var rule = vm.ToClientRule(Guid.NewGuid());

        Assert.Equal(["billing@acme.com"], rule.FromAddresses);
        Assert.True(rule.UseFromCondition);
        Assert.Equal("acme", rule.SenderContains);
        Assert.True(rule.UseSenderCondition);
    }

    [Fact]
    public void WhenTheAddressesAreSwitchedOff_SenderContainsTakesTheSharedField()
    {
        // The live condition owns the field an older build reads. Leaving that field holding a
        // switched-off address list would leave such a build testing the From header on nothing at all,
        // and a Move rule there would act on every message.
        var vm = Editor();
        vm.UseSenderContains = true;
        vm.SenderContains = "acme";
        vm.FromAddresses = "billing@x.com, invoices@y.com";
        vm.UseFromAddresses = false;

        var rule = vm.ToClientRule(Guid.NewGuid());

        Assert.True(rule.UseFromCondition);
        Assert.Equal("acme", rule.FromContains);
        Assert.Null(rule.FromAddresses);
        Assert.True(Matches(rule, Msg(from: "anyone@acme.com")));
        Assert.False(Matches(rule, Msg(from: "billing@x.com")));

        // And it reads back as the sender condition it is, not relabelled as a From address.
        var reopened = ServerRuleEditorViewModel.ForEditClient(rule);
        Assert.Equal("acme", reopened.SenderContains);
        Assert.True(reopened.UseSenderContains);
        Assert.Equal(string.Empty, reopened.FromAddresses);
    }

    [Fact]
    public void SubjectOrBody_IsSavedAsTheSubjectConditionWidened()
    {
        var vm = Editor();
        vm.UseBodyOrSubjectContains = true;
        vm.BodyOrSubjectContains = "invoice";

        var rule = vm.ToClientRule(Guid.NewGuid());

        Assert.Equal("invoice", rule.SubjectContains);
        Assert.True(rule.UseSubjectCondition);
        Assert.True(rule.SubjectAlsoMatchesBody);
    }

    [Fact]
    public void SubjectAndSubjectOrBodyTogether_CantBeAClientRule()
    {
        var vm = Editor();
        vm.UseSubjectContains = true;
        vm.SubjectContains = "Digest";
        vm.UseBodyOrSubjectContains = true;
        vm.BodyOrSubjectContains = "invoice";

        Assert.False(vm.IsClientRepresentable);

        var conflict = vm.Classify(accountSupportsServerRules: false);
        Assert.True(conflict.IsConflict);
        // The message must not send the user hunting for a setting: this is the client rule model's own
        // limit, not a server feature they could go and find.
        Assert.Contains("Subject or body contains", conflict.ConflictError!, StringComparison.Ordinal);
        Assert.DoesNotContain("isn't available in a client-side rule", conflict.ConflictError!, StringComparison.Ordinal);
    }

    [Fact]
    public void OnAnAccountWithClientRulesOnly_TheNewConditionsSaveWithoutAFight()
    {
        // The report behind #682: an IMAP account was offered these and only told on Save.
        var vm = Editor();
        vm.UseSenderContains = true;
        vm.SenderContains = "acme";
        vm.UseFromAddresses = true;
        vm.FromAddresses = "billing@acme.com, invoices@acme.net";
        vm.UseSentToAddresses = true;
        vm.SentToAddresses = "me@work.com, team@work.com";

        Assert.True(vm.Validate());
        Assert.Equal(RuleRunsWhere.Client, vm.Classify(accountSupportsServerRules: false).Kind);
    }

    [Fact]
    public void OnAMicrosoft365Account_TheyStillPreferAServerRule()
    {
        var vm = Editor();
        vm.UseFromAddresses = true;
        vm.FromAddresses = "billing@x.com, invoices@y.com";

        Assert.Equal(RuleRunsWhere.Server, vm.Classify(accountSupportsServerRules: true).Kind);
    }

    // ── Reopening a saved rule ──────────────────────────────────────────────

    [Fact]
    public void EveryNewConditionSurvivesASaveAndReopen()
    {
        var vm = Editor();
        vm.UseSenderContains = true;
        vm.SenderContains = "acme";
        vm.UseFromAddresses = true;
        vm.FromAddresses = "billing@acme.com, invoices@acme.net";
        vm.UseSentToAddresses = true;
        vm.SentToAddresses = "me@work.com, team@work.com";
        vm.UseBodyOrSubjectContains = true;
        vm.BodyOrSubjectContains = "invoice";

        var reopened = ServerRuleEditorViewModel.ForEditClient(vm.ToClientRule(Guid.NewGuid()));

        Assert.Equal("acme", reopened.SenderContains);
        Assert.True(reopened.UseSenderContains);
        Assert.Equal("billing@acme.com, invoices@acme.net", reopened.FromAddresses);
        Assert.True(reopened.UseFromAddresses);
        Assert.Equal("me@work.com, team@work.com", reopened.SentToAddresses);
        Assert.True(reopened.UseSentToAddresses);
        // Back in the box it was typed in, not in the plain Subject box beside it.
        Assert.Equal("invoice", reopened.BodyOrSubjectContains);
        Assert.True(reopened.UseBodyOrSubjectContains);
        Assert.Equal(string.Empty, reopened.SubjectContains);
        Assert.False(reopened.UseSubjectContains);
    }

    [Fact]
    public void ASwitchedOffAddressList_KeepsItsTextOnReopening()
    {
        // #665: switching a condition off leaves its text one keystroke from being used again, and that
        // has to hold for a whole list, not just a single address.
        var vm = Editor();
        vm.FromAddresses = "billing@x.com, invoices@y.com";
        vm.UseFromAddresses = false;
        vm.UseSubjectContains = true;
        vm.SubjectContains = "Digest";

        var rule = vm.ToClientRule(Guid.NewGuid());
        Assert.False(rule.UseFromCondition);

        var reopened = ServerRuleEditorViewModel.ForEditClient(rule);
        Assert.Equal("billing@x.com, invoices@y.com", reopened.FromAddresses);
        Assert.False(reopened.UseFromAddresses);
    }

    [Fact]
    public void WithNeitherFromConditionLive_BothTextsComeBack()
    {
        // #665 again: two switched-off boxes, two texts to offer back. Neither owns the old field, so
        // neither has to give up its own.
        var vm = Editor();
        vm.SenderContains = "acme";
        vm.UseSenderContains = false;
        vm.FromAddresses = "billing@x.com, invoices@y.com";
        vm.UseFromAddresses = false;
        vm.UseSubjectContains = true;
        vm.SubjectContains = "Digest";

        var rule = vm.ToClientRule(Guid.NewGuid());
        Assert.False(rule.UseFromCondition);
        Assert.Empty(rule.FromValues());

        var reopened = ServerRuleEditorViewModel.ForEditClient(rule);
        Assert.Equal("acme", reopened.SenderContains);
        Assert.False(reopened.UseSenderContains);
        Assert.Equal("billing@x.com, invoices@y.com", reopened.FromAddresses);
        Assert.False(reopened.UseFromAddresses);
    }

    [Fact]
    public void ARuleFromAnEarlierVersion_OpensWithItsConditionInTheSameBox()
    {
        var reopened = ServerRuleEditorViewModel.ForEditClient(new MailRule
        {
            Name = "Legacy",
            UseFromCondition = true,
            FromContains = "alice@example.com",
            UseSubjectCondition = true,
            SubjectContains = "Report",
            Action = RuleAction.MarkAsRead,
        });

        Assert.Equal("alice@example.com", reopened.FromAddresses);
        Assert.Equal("Report", reopened.SubjectContains);
        Assert.True(reopened.UseSubjectContains);
        Assert.Equal(string.Empty, reopened.BodyOrSubjectContains);
    }

    [Fact]
    public void ASenderWhoseNameHoldsAComma_IsOneConditionNotTwo()
    {
        // MailMessageSummary.From is the sender's DISPLAY NAME where there is one, and an Exchange
        // address book routinely makes that "Last, First". Carried into an address field, the comma
        // would be read as a separator and "Rule for Ford, Kelly" would file mail from anyone called
        // Ford as well as anyone called Kelly. The prefill is a substring match on the sender instead.
        var vm = ServerRuleEditorViewModel.ForNewFromTemplate(new MailRule
        {
            Name = "Rule for Ford, Kelly",
            SenderContains = "Ford, Kelly",
            UseSenderCondition = true,
        });
        vm.MoveToFolder = true;
        vm.MoveToFolderId = "Archive";

        Assert.Equal("Ford, Kelly", vm.SenderContains);
        Assert.Equal(string.Empty, vm.FromAddresses);
        Assert.True(vm.Validate());

        var rule = vm.ToClientRule(Guid.NewGuid());
        Assert.Null(rule.FromAddresses);
        Assert.True(Matches(rule, Msg(from: "Ford, Kelly")));
        Assert.False(Matches(rule, Msg(from: "Ford, Harrison")));
        Assert.False(Matches(rule, Msg(from: "Green, Kelly")));
    }

    [Fact]
    public void ALegacyRuleWhoseSenderHoldsAComma_IsNotSplitInTwoByEditingIt()
    {
        // The population at risk is the one Ctrl+Shift+T itself created: every rule it wrote before
        // this change holds the sender's display name in the old single field, and an Exchange address
        // book routinely renders that "Last, First". Opening such a rule to rename it and saving must
        // not turn "from Ford, Kelly" into "from anyone called Ford or anyone called Kelly".
        var legacy = new MailRule
        {
            Name = "Rule for Ford, Kelly",
            UseFromCondition = true,
            FromContains = "Ford, Kelly",
            UseSubjectCondition = false,
            Action = RuleAction.MoveToFolder,
            TargetFolder = "Archive",
        };
        Assert.True(Matches(legacy, Msg(from: "Ford, Kelly")));
        Assert.False(Matches(legacy, Msg(from: "Ford, Harrison")));

        var reopened = ServerRuleEditorViewModel.ForEditClient(legacy);
        Assert.Equal("Ford, Kelly", reopened.SenderContains);
        Assert.Equal(string.Empty, reopened.FromAddresses);

        var resaved = reopened.ToClientRule(Guid.NewGuid());
        Assert.Null(resaved.FromAddresses);
        Assert.Equal("Ford, Kelly", resaved.FromContains);   // what an older build still reads
        Assert.True(Matches(resaved, Msg(from: "Ford, Kelly")));
        Assert.False(Matches(resaved, Msg(from: "Ford, Harrison")));
        Assert.False(Matches(resaved, Msg(from: "Green, Kelly")));
    }

    [Fact]
    public void ThePrefilledSenderIsInTheEditorsMainSection_NotBehindAdvanced()
    {
        // Sender contains and From addresses swapped places for this (#682): the field Create Rule
        // from Message fills is the one the user meets first, and an address list a rule made by hand
        // needs is the less common of the two.
        var vm = ServerRuleEditorViewModel.ForNewFromTemplate(new MailRule
        {
            Name = "Rule for boss@work.com",
            SenderContains = "boss@work.com",
            UseSenderCondition = true,
            SubjectContains = "Weekly Report",
            UseSubjectCondition = false,
        });

        Assert.False(vm.IsAdvancedExpanded);

        // And the list that decides it still matches where the fields actually are: a rule using the
        // address list opens with Advanced showing, so editing never hides a field that is in use.
        var withAddresses = ServerRuleEditorViewModel.ForEditClient(new MailRule
        {
            Name = "Billing",
            UseFromCondition = true,
            FromContains = "billing@x.com",
            FromAddresses = ["billing@x.com", "invoices@y.com"],
            Action = RuleAction.MarkAsRead,
        });
        Assert.True(withAddresses.IsAdvancedExpanded);
    }

    [Fact]
    public void AServerRuleRowSpeaksItsAddressesTheSameWayAClientRuleRowDoes()
    {
        // Both kinds are read from the one list, so they must not describe the same condition in two
        // different ways.
        var server = UnifiedRuleRow.ForServer(new ServerRuleModel
        {
            DisplayName = "Billing",
            IsEnabled = true,
            FromAddresses = ["billing@x.com", "invoices@y.com"],
            MarkAsRead = true,
        });

        Assert.Contains("from any of billing@x.com, invoices@y.com", server.RowText, StringComparison.Ordinal);
        Assert.DoesNotContain(" or ", server.RowText, StringComparison.Ordinal);
    }

    [Fact]
    public void EditingAClientRuleIntoTheSubjectClash_SaysWhichTwoConditionsClash()
    {
        // The edit path used to answer "remove the conditions client-side rules don't support", which
        // sends the user looking for an unsupported condition when both of theirs are supported and it
        // is the pair that isn't (#682).
        var vm = ServerRuleEditorViewModel.ForEditClient(new MailRule
        {
            Name = "Digests",
            UseFromCondition = false,
            UseSubjectCondition = true,
            SubjectContains = "Digest",
            Action = RuleAction.MarkAsRead,
        });
        // As the editor does it: the box is read-only until its checkbox is ticked.
        vm.UseBodyOrSubjectContains = true;
        vm.BodyOrSubjectContains = "invoice";

        var error = vm.ClientEditError;

        Assert.NotNull(error);
        Assert.Contains("Subject contains and Subject or body contains", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void ARuleWithTheNewConditions_SurvivesTheRulesFile()
    {
        // In memory is not where a rule lives. A wrong JsonIgnore condition on any of the new fields
        // would drop a live condition on the next load — silently widening a rule that moves mail —
        // and every other test here would stay green.
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var svc = new RuleService(new StubImapMailService(), new StubLocalStoreService(), dir);
            var vm = Editor("Round trip");
            vm.UseSenderContains = true;
            vm.SenderContains = "acme";
            vm.UseFromAddresses = true;
            vm.FromAddresses = "billing@acme.com, invoices@acme.net";
            vm.UseSentToAddresses = true;
            vm.SentToAddresses = "me@work.com, team@work.com";
            vm.UseBodyOrSubjectContains = true;
            vm.BodyOrSubjectContains = "invoice";
            svc.SaveRules([vm.ToClientRule(Guid.NewGuid())]);

            var reloaded = new RuleService(new StubImapMailService(), new StubLocalStoreService(), dir)
                .LoadRules().Single();

            Assert.Equal(["billing@acme.com", "invoices@acme.net"], reloaded.FromAddresses);
            Assert.Equal(["me@work.com", "team@work.com"], reloaded.SentToAddresses);
            Assert.Equal("acme", reloaded.SenderContains);
            Assert.True(reloaded.UseSenderCondition);
            Assert.True(reloaded.SubjectAlsoMatchesBody);

            // What a QuickMail from before #682 reads from the same file: one of the addresses, and
            // the subject half of subject-or-body. Both match less mail than the rule covers.
            var json = File.ReadAllText(Path.Combine(dir, "rules.json"));
            Assert.Contains("\"FromContains\": \"billing@acme.com\"", json, StringComparison.Ordinal);
            Assert.Contains("\"ToContains\": \"me@work.com\"", json, StringComparison.Ordinal);
            Assert.Contains("\"SubjectContains\": \"invoice\"", json, StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // ── What the rules list says ────────────────────────────────────────────

    [Fact]
    public void TheRowSpeaksTheChoiceOfAddresses_AndWhereTheTextIsLookedFor()
    {
        var row = UnifiedRuleRow.ForClient(new MailRule
        {
            Name = "Billing",
            IsEnabled = true,
            UseFromCondition = true,
            FromContains = "billing@x.com",
            FromAddresses = ["billing@x.com", "invoices@y.com"],
            UseSenderCondition = true,
            SenderContains = "acme",
            UseSubjectCondition = true,
            SubjectContains = "invoice",
            SubjectAlsoMatchesBody = true,
            Action = RuleAction.MarkAsRead,
        });

        // "any of", not "a or b": spoken beside the "and" that joins the conditions, "or" leaves the
        // grouping ambiguous, and the reading that wins is the one where the rule acts more widely.
        Assert.Contains("from contains any of 'billing@x.com', 'invoices@y.com'", row.RowText, StringComparison.Ordinal);
        Assert.Contains("sender contains 'acme'", row.RowText, StringComparison.Ordinal);
        Assert.Contains("subject or body contains 'invoice'", row.RowText, StringComparison.Ordinal);
    }

    [Fact]
    public void ARuleWithOneAddress_ReadsExactlyAsItAlwaysHas()
    {
        var row = UnifiedRuleRow.ForClient(new MailRule
        {
            Name = "Billing",
            IsEnabled = true,
            UseFromCondition = true,
            FromContains = "billing@x.com",
            UseSubjectCondition = false,
            Action = RuleAction.MarkAsRead,
        });

        Assert.Contains("If from contains 'billing@x.com' →", row.RowText, StringComparison.Ordinal);
        Assert.DoesNotContain("any of", row.RowText, StringComparison.Ordinal);
    }
}
