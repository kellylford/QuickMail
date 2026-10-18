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
        Assert.Contains("from addresses (an entry without an address)", model.UnsupportedFields);
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
