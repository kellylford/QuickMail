using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using QuickMail.Models;
using QuickMail.Services.Graph;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Issue #810, finding 2: server rules kept a recipient's address and dropped its display name, so every
/// save from QuickMail stripped the names from a rule made in Outlook, and Outlook Classic then switched
/// such rules off. These pin the name through the mapper, the model and the editor, in both directions.
/// </summary>
public class ServerRuleRecipientNameTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static GraphMessageRule Dto(string conditionsJson, string actionsJson = """{ "moveToFolder": "F1" }""")
        => new()
        {
            Id = "r1",
            DisplayName = "Rule",
            Sequence = 1,
            IsEnabled = true,
            Conditions = JsonDocument.Parse(conditionsJson).RootElement.Clone(),
            Actions = JsonDocument.Parse(actionsJson).RootElement.Clone(),
        };

    /// <summary>The request body exactly as it goes on the wire.</summary>
    private static JsonElement Wire(ServerRuleModel rule)
        => JsonDocument.Parse(JsonSerializer.Serialize(ServerRuleMapper.ToRequestBody(rule))).RootElement.Clone();

    /// <summary>The (name, address) pairs of a recipient collection in a request body.</summary>
    private static List<(string? Name, string? Address)> Entries(JsonElement collection)
        => collection.EnumerateArray()
            .Select(e => e.GetProperty("emailAddress"))
            .Select(ea => (ea.TryGetProperty("name", out var n) ? n.GetString() : null,
                           ea.TryGetProperty("address", out var a) ? a.GetString() : null))
            .ToList();

    private const string RocketConditions = """
        { "fromAddresses": [
            { "emailAddress": { "name": "Rocket Mortgage Servicing", "address": "servicing@rocket.example" } },
            { "emailAddress": { "name": "Rocket Mortgage Servicing", "address": "noreply@rocket.example" } } ] }
        """;

    // ── Graph → model ────────────────────────────────────────────────────────

    [Fact]
    public void ToModel_KeepsDisplayNames_OnEveryRecipientCollection()
    {
        var model = ServerRuleMapper.ToModel(Dto(
            """
            { "fromAddresses": [ { "emailAddress": { "name": "Boss", "address": "boss@contoso.com" } } ],
              "sentToAddresses": [ { "emailAddress": { "name": "BITS", "address": "bits@bits.groups.io" } } ] }
            """,
            """{ "forwardTo": [ { "emailAddress": { "name": "Me at home", "address": "me@home.example" } } ] }"""));

        Assert.True(model.IsFullyEditable);
        Assert.Equal(new RuleRecipient("boss@contoso.com", "Boss"), Assert.Single(model.FromAddresses));
        Assert.Equal(new RuleRecipient("bits@bits.groups.io", "BITS"), Assert.Single(model.SentToAddresses));
        Assert.Equal(new RuleRecipient("me@home.example", "Me at home"), Assert.Single(model.ForwardTo));
    }

    [Fact]
    public void ToModel_ARecipientWithNoAddress_MakesTheRuleViewOnly()
    {
        // The model cannot hold an entry without an address, so saving would drop it. Gate the rule rather
        // than lose part of it silently (spec §16).
        var model = ServerRuleMapper.ToModel(Dto(
            """
            { "fromAddresses": [
                { "emailAddress": { "name": "Has one", "address": "a@contoso.com" } },
                { "emailAddress": { "name": "Has none" } } ] }
            """));

        Assert.False(model.IsFullyEditable);
        Assert.Contains("from addresses (an entry QuickMail can't represent)", model.UnsupportedFields);
    }

    [Theory]
    [InlineData("""{ "sentToAddresses": [ { "emailAddress": { "name": "No address" } } ] }""", """{ "moveToFolder": "F1" }""", "sent to addresses")]
    [InlineData("""{ "subjectContains": ["x"] }""", """{ "forwardTo": [ { "emailAddress": { "address": "" } } ] }""", "forward to")]
    [InlineData("""{ "fromAddresses": [ "not-an-object" ] }""", """{ "moveToFolder": "F1" }""", "from addresses")]
    [InlineData("""{ "fromAddresses": [ { "emailAddress": { "name": "Ford, Kelly", "address": "Ford, Kelly" } } ] }""", """{ "moveToFolder": "F1" }""", "from addresses")]
    public void ToModel_ARecipientTheEditorCannotHold_MakesTheRuleViewOnly(string conditions, string actions, string field)
    {
        // An address holding a comma (an unresolved Outlook entry stores the name there) would come back
        // from the editor as two entries; one with no address would be dropped. Either is a silent rewrite.
        var model = ServerRuleMapper.ToModel(Dto(conditions, actions));

        Assert.False(model.IsFullyEditable);
        Assert.Contains($"{field} (an entry QuickMail can't represent)", model.UnsupportedFields);
    }

    // ── Model → Graph ────────────────────────────────────────────────────────

    [Fact]
    public void ToRequestBody_SendsTheNameWithEveryAddress()
    {
        var body = Wire(new ServerRuleModel
        {
            DisplayName = "Rule",
            FromAddresses = [new("boss@contoso.com", "Boss")],
            SentToAddresses = [new("bits@bits.groups.io", "BITS")],
            ForwardTo = [new("me@home.example", "Me at home")],
        });

        Assert.Equal([("Boss", "boss@contoso.com")], Entries(body.GetProperty("conditions").GetProperty("fromAddresses")));
        Assert.Equal([("BITS", "bits@bits.groups.io")], Entries(body.GetProperty("conditions").GetProperty("sentToAddresses")));
        Assert.Equal([("Me at home", "me@home.example")], Entries(body.GetProperty("actions").GetProperty("forwardTo")));
    }

    [Fact]
    public void ToRequestBody_AnEntryWithNoName_SendsTheAddressAsItsName()
    {
        // The form Exchange uses for Outlook's own address-only entries. An entry with no name at all is
        // what Outlook Classic switched off (#810).
        var body = Wire(new ServerRuleModel
        {
            DisplayName = "BITS",
            SentToAddresses = [new("bits@bits.groups.io")],
        });

        Assert.Equal([("bits@bits.groups.io", "bits@bits.groups.io")],
            Entries(body.GetProperty("conditions").GetProperty("sentToAddresses")));
    }

    // ── Round trips through the editor ───────────────────────────────────────

    [Fact]
    public void ARuleMadeInOutlook_KeepsItsNames_WhenEditedAndSaved()
    {
        // The reported case: editing a rule made in Outlook stripped the name from every entry.
        var model = ServerRuleMapper.ToModel(Dto(RocketConditions));
        var editor = ServerRuleEditorViewModel.ForEdit(model);
        editor.Name = "Rocket, renamed";

        var body = Wire(editor.ToModel());

        Assert.Equal(
            [("Rocket Mortgage Servicing", "servicing@rocket.example"), ("Rocket Mortgage Servicing", "noreply@rocket.example")],
            Entries(body.GetProperty("conditions").GetProperty("fromAddresses")));
    }

    [Fact]
    public void TheEditor_ShowsEachRecipientsName()
    {
        var editor = ServerRuleEditorViewModel.ForEdit(ServerRuleMapper.ToModel(Dto(RocketConditions)));

        Assert.Equal(
            "Rocket Mortgage Servicing <servicing@rocket.example>, Rocket Mortgage Servicing <noreply@rocket.example>",
            editor.FromAddresses);
    }

    [Fact]
    public void ANameEqualToItsAddress_ShowsAsTheBareAddress_AndSavesUnchanged()
    {
        var model = ServerRuleMapper.ToModel(Dto(
            """{ "sentToAddresses": [ { "emailAddress": { "name": "bits@bits.groups.io", "address": "bits@bits.groups.io" } } ] }"""));
        var editor = ServerRuleEditorViewModel.ForEdit(model);

        Assert.Equal("bits@bits.groups.io", editor.SentToAddresses);
        Assert.Equal([("bits@bits.groups.io", "bits@bits.groups.io")],
            Entries(Wire(editor.ToModel()).GetProperty("conditions").GetProperty("sentToAddresses")));
    }

    [Fact]
    public void ANameHoldingAComma_StaysOneRecipient_ThroughTheEditor()
    {
        // An Exchange address book writes people "Last, First". Split at the comma, one recipient would
        // come back as two.
        var model = ServerRuleMapper.ToModel(Dto(
            """{ "fromAddresses": [ { "emailAddress": { "name": "Ford, Kelly", "address": "kelly@contoso.com" } } ] }"""));
        var editor = ServerRuleEditorViewModel.ForEdit(model);

        Assert.Equal("\"Ford, Kelly\" <kelly@contoso.com>", editor.FromAddresses);
        Assert.Equal([("Ford, Kelly", "kelly@contoso.com")],
            Entries(Wire(editor.ToModel()).GetProperty("conditions").GetProperty("fromAddresses")));
    }

    [Fact]
    public void AddingAnAddressToAnOutlookRule_KeepsTheOtherNames_AndNamesTheNewOneByItsAddress()
    {
        var editor = ServerRuleEditorViewModel.ForEdit(ServerRuleMapper.ToModel(Dto(
            """{ "fromAddresses": [ { "emailAddress": { "name": "Boss", "address": "boss@contoso.com" } } ] }""")));
        editor.FromAddresses += ", new@contoso.com";

        Assert.Equal([("Boss", "boss@contoso.com"), ("new@contoso.com", "new@contoso.com")],
            Entries(Wire(editor.ToModel()).GetProperty("conditions").GetProperty("fromAddresses")));
    }

    [Fact]
    public void ATypedNameAndAddress_IsSavedWithThatName()
    {
        var editor = ServerRuleEditorViewModel.ForNew();
        editor.Name = "Forward";
        editor.ForwardTo = "Home <me@home.example>; work@contoso.com";

        Assert.Equal([("Home", "me@home.example"), ("work@contoso.com", "work@contoso.com")],
            Entries(Wire(editor.ToModel()).GetProperty("actions").GetProperty("forwardTo")));
    }

    [Fact]
    public void AClientRule_MatchesTheAddressOfANamedEntry_NotTheWholeText()
    {
        // A client rule has addresses only. "Name <address>" typed into a new rule that lands client-side
        // must match on the address, not on a string no From header contains.
        var editor = ServerRuleEditorViewModel.ForNew();
        editor.Name = "Boss";
        editor.UseFromAddresses = true;
        editor.FromAddresses = "\"Ford, Kelly\" <kelly@contoso.com>, Boss <boss@contoso.com>";
        editor.MarkAsUnread = true;

        var rule = editor.ToClientRule(Guid.NewGuid());

        Assert.Equal(["kelly@contoso.com", "boss@contoso.com"], rule.FromAddresses);
    }

    [Fact]
    public void ANameDifferingFromItsAddressOnlyInCase_IsKeptAsItIs()
    {
        var model = ServerRuleMapper.ToModel(Dto(
            """{ "fromAddresses": [ { "emailAddress": { "name": "Kelly@Contoso.com", "address": "kelly@contoso.com" } } ] }"""));

        Assert.Equal([("Kelly@Contoso.com", "kelly@contoso.com")],
            Entries(Wire(ServerRuleEditorViewModel.ForEdit(model).ToModel()).GetProperty("conditions").GetProperty("fromAddresses")));
    }

    [Theory]
    [InlineData("a@b.com, \"c@d.com, e@f.com", "From any of these addresses has a quotation mark or < that is never closed.")]
    [InlineData("Bob <bob@x.com, c@d.com", "From any of these addresses has a quotation mark or < that is never closed.")]
    [InlineData("a@b.com, Nobody <>", "From any of these addresses has an entry with no address.")]
    public void AnAddressBoxThatCantBeReadAsItLooks_IsRefusedOnSave(string from, string error)
    {
        // Read quietly, these would save one long "address" that matches nothing, or lose an entry.
        var editor = ServerRuleEditorViewModel.ForNew();
        editor.Name = "Rule";
        editor.UseSubjectContains = true;
        editor.SubjectContains = "x";
        editor.UseFromAddresses = true;
        editor.FromAddresses = from;
        editor.Delete = true;
        string? announced = null;
        editor.AnnouncementRequested += (text, _) => announced = text;

        Assert.False(editor.Validate());
        Assert.Equal(error, editor.AddressError);
        Assert.Contains(error, announced, StringComparison.Ordinal);
    }

    [Fact]
    public void AProblemInASwitchedOffAddressBox_DoesNotBlockSaving()
    {
        var editor = ServerRuleEditorViewModel.ForNew();
        editor.Name = "Rule";
        editor.UseSubjectContains = true;
        editor.SubjectContains = "x";
        editor.UseFromAddresses = false;
        editor.FromAddresses = "Bob <bob@x.com";
        editor.Delete = true;

        Assert.True(editor.Validate());
        Assert.Equal(string.Empty, editor.AddressError);
    }

    [Fact]
    public void AnUnclosedForwardTo_IsRefusedOnSave()
    {
        var editor = ServerRuleEditorViewModel.ForNew();
        editor.Name = "Rule";
        editor.ForwardTo = "\"Home <me@home.example>";

        Assert.False(editor.Validate());
        Assert.Equal("Forward to has a quotation mark or < that is never closed.", editor.AddressError);
    }

    [Fact]
    public void ABareQuotedEntry_MeansTheTextInsideTheQuotes()
    {
        // In a client rule's From box, a display name with a comma is written in quotes so the comma
        // separates nothing; the quotation marks themselves are not part of what to match.
        Assert.Equal([new RuleRecipient("Ford, Kelly")], RuleRecipient.ParseList("\"Ford, Kelly\""));
    }

    // ── Presentation ─────────────────────────────────────────────────────────

    [Fact]
    public void TheRuleSummary_SaysTheNameAndTheAddress()
    {
        var model = ServerRuleMapper.ToModel(Dto(
            """{ "fromAddresses": [ { "emailAddress": { "name": "Boss", "address": "boss@contoso.com" } } ] }"""));

        Assert.Contains("from Boss (boss@contoso.com)", model.OneLineSummary(), StringComparison.Ordinal);
    }

    // ── RuleRecipient.ParseList ──────────────────────────────────────────────

    [Fact]
    public void ParseList_ReadsEveryForm()
    {
        var list = RuleRecipient.ParseList(
            "a@x.com; Bob <bob@x.com>, \"Ford, Kelly\" <k@x.com>,, \"Say \\\"hi\\\"\" <q@x.com> ; Nobody <>");

        Assert.Equal(
            [
                new RuleRecipient("a@x.com"),
                new RuleRecipient("bob@x.com", "Bob"),
                new RuleRecipient("k@x.com", "Ford, Kelly"),
                new RuleRecipient("q@x.com", "Say \"hi\""),
            ],
            list);
    }

    [Fact]
    public void FormatList_ThenParseList_RoundTrips()
    {
        List<RuleRecipient> original =
        [
            new("a@x.com"),
            new("bob@x.com", "Bob"),
            new("k@x.com", "Ford, Kelly"),
            new("q@x.com", "Say \"hi\"; <loud>"),
        ];

        Assert.Equal(original, RuleRecipient.ParseList(RuleRecipient.FormatList(original)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(", ;")]
    public void ParseList_OfNothing_IsEmpty(string text)
        => Assert.Empty(RuleRecipient.ParseList(text));
}
