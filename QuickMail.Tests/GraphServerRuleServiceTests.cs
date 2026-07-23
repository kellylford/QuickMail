using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.Services.Graph;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Server-side rules over the Graph messageRule API: mapping, the round-trip-safety gating that
/// keeps us from silently deleting predicates we don't model (spec §16), request shapes for
/// create/update/toggle/reorder/delete, and the 403 → consent-exception translation.
/// HTTP is stubbed with a queued handler (the GraphClientTests style); no live Graph.
/// </summary>
public class GraphServerRuleServiceTests
{
    private readonly Guid _accountId = Guid.NewGuid();

    // ── Test plumbing ────────────────────────────────────────────────────────────

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public List<string> Urls { get; } = [];
        public List<string> Methods { get; } = [];
        public List<string?> Bodies { get; } = [];

        public RecordingHandler(params HttpResponseMessage[] responses)
            => _responses = new Queue<HttpResponseMessage>(responses);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            Methods.Add(request.Method.Method);
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
            return _responses.Count > 0 ? _responses.Dequeue() : Json("""{ "value": [] }""");
        }
    }

    private sealed class FixedAccountService : IAccountService
    {
        private readonly List<AccountModel> _accounts;
        public FixedAccountService(params AccountModel[] accounts) => _accounts = [.. accounts];
        public List<AccountModel> LoadAccounts() => _accounts;
        public void SaveAccounts(List<AccountModel> accounts) { }
        public void SetDefaultAccount(Guid accountId) { }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private AccountModel GraphAccount() => new()
    {
        Id = _accountId,
        BackendKind = BackendKind.MicrosoftGraph,
        Username = "user@contoso.com",
    };

    private GraphServerRuleService Service(RecordingHandler handler)
        => new(new FixedAccountService(GraphAccount()),
               new GraphClient(new StubOAuthService(), new HttpClient(handler), defaultRetryDelay: TimeSpan.Zero));

    private static string Collection(params string[] rules) => $$"""{ "value": [ {{string.Join(",", rules)}} ] }""";

    // ── List & mapping ───────────────────────────────────────────────────────────

    [Fact]
    public async Task List_MapsFields_AndOrdersBySequence()
    {
        var handler = new RecordingHandler(Json(Collection(
            """
            { "id": "r2", "displayName": "Second", "sequence": 2, "isEnabled": false,
              "isReadOnly": true, "hasError": true,
              "conditions": { "subjectContains": ["invoice"] },
              "actions": { "markAsRead": true } }
            """,
            """
            { "id": "r1", "displayName": "First", "sequence": 1, "isEnabled": true,
              "conditions": { "senderContains": ["newsletter"] },
              "actions": { "moveToFolder": "AAMkFolderId" } }
            """)));

        var rules = await Service(handler).ListAsync(_accountId);

        Assert.Equal(2, rules.Count);
        Assert.Equal(["r1", "r2"], rules.Select(r => r.Id));   // sorted by sequence, not payload order
        Assert.Contains("messageRules", handler.Urls[0]);

        var first = rules[0];
        Assert.Equal("First", first.DisplayName);
        Assert.True(first.IsEnabled);
        Assert.Equal("newsletter", first.SenderContains);
        Assert.Equal("AAMkFolderId", first.MoveToFolderId);
        Assert.True(first.IsFullyEditable);

        var second = rules[1];
        Assert.False(second.IsEnabled);
        Assert.True(second.IsReadOnly);
        Assert.True(second.HasError);
        Assert.Equal("invoice", second.SubjectContains);
        Assert.True(second.MarkAsRead);
    }

    [Fact]
    public async Task List_NullEmptyAndFalsePredicates_DoNotMakeRuleUneditable()
    {
        // Graph returns unset predicates as null / [] / false. Treating those as "present" would
        // flag essentially every rule as non-editable.
        var handler = new RecordingHandler(Json(Collection(
            """
            { "id": "r1", "displayName": "Sparse", "sequence": 1, "isEnabled": true,
              "conditions": { "subjectContains": ["hi"], "bodyContains": null,
                              "categories": [], "sentCcMe": false },
              "actions": { "markAsRead": true, "assignCategories": null },
              "exceptions": { "subjectContains": null } }
            """)));

        var rule = (await Service(handler).ListAsync(_accountId)).Single();

        Assert.True(rule.IsFullyEditable);
        Assert.Empty(rule.UnsupportedFields);
    }

    [Fact]
    public async Task List_UnsupportedPredicate_MarksRuleViewOnly()
    {
        var handler = new RecordingHandler(Json(Collection(
            """
            { "id": "r1", "displayName": "Complex", "sequence": 1, "isEnabled": true,
              "conditions": { "subjectContains": ["x"], "bodyContains": ["secret"] },
              "actions": { "markAsRead": true } }
            """)));

        var rule = (await Service(handler).ListAsync(_accountId)).Single();

        Assert.False(rule.IsFullyEditable);
        Assert.Contains("body contains", rule.UnsupportedFields);
    }

    [Fact]
    public async Task List_UnsupportedAction_MarksRuleViewOnly()
    {
        var handler = new RecordingHandler(Json(Collection(
            """
            { "id": "r1", "displayName": "Redirects", "sequence": 1, "isEnabled": true,
              "conditions": { "subjectContains": ["x"] },
              "actions": { "redirectTo": [ { "emailAddress": { "address": "a@b.com" } } ] } }
            """)));

        var rule = (await Service(handler).ListAsync(_accountId)).Single();

        Assert.False(rule.IsFullyEditable);
        Assert.Contains("redirect to", rule.UnsupportedFields);
    }

    [Fact]
    public async Task List_RuleWithExceptions_IsViewOnly()
    {
        // We never author exceptions, so a rule that has them can't be safely rewritten.
        var handler = new RecordingHandler(Json(Collection(
            """
            { "id": "r1", "displayName": "Has exceptions", "sequence": 1, "isEnabled": true,
              "conditions": { "subjectContains": ["x"] },
              "actions": { "markAsRead": true },
              "exceptions": { "subjectContains": ["skip"] } }
            """)));

        var rule = (await Service(handler).ListAsync(_accountId)).Single();

        Assert.False(rule.IsFullyEditable);
        Assert.Contains("exceptions", rule.UnsupportedFields);
    }

    [Fact]
    public async Task List_ExtractsRecipientAddresses()
    {
        var handler = new RecordingHandler(Json(Collection(
            """
            { "id": "r1", "displayName": "Fwd", "sequence": 1, "isEnabled": true,
              "conditions": { "fromAddresses": [ { "emailAddress": { "address": "boss@contoso.com", "name": "Boss" } } ] },
              "actions": { "forwardTo": [ { "emailAddress": { "address": "me@contoso.com" } } ] } }
            """)));

        var rule = (await Service(handler).ListAsync(_accountId)).Single();

        Assert.Equal("boss@contoso.com", Assert.Single(rule.FromAddresses));
        Assert.Equal("me@contoso.com", Assert.Single(rule.ForwardTo));
        Assert.True(rule.IsFullyEditable);
    }

    // ── Round-trip safety (spec §16) ─────────────────────────────────────────────

    [Fact]
    public async Task Update_RefusesRuleThatIsNotFullyEditable()
    {
        // The critical guard: PATCH replaces conditions/actions wholesale, so saving a rule we only
        // partially model would silently delete the user's other predicates.
        var handler = new RecordingHandler();
        var rule = new ServerRuleModel
        {
            Id = "r1",
            DisplayName = "Complex",
            IsFullyEditable = false,
            UnsupportedFields = ["body contains"],
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(handler).UpdateAsync(_accountId, rule));

        Assert.Contains("Outlook", ex.Message);
        Assert.Empty(handler.Urls); // nothing was sent to Graph
    }

    // ── Write paths ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_PostsRuleBody_AndReturnsServerCopy()
    {
        var handler = new RecordingHandler(Json(
            """
            { "id": "new-id", "displayName": "Newsletters", "sequence": 1, "isEnabled": true,
              "conditions": { "senderContains": ["news"] },
              "actions": { "moveToFolder": "FolderX" } }
            """));

        var created = await Service(handler).CreateAsync(_accountId, new ServerRuleModel
        {
            DisplayName = "Newsletters",
            Sequence = 1,
            SenderContains = "news",
            MoveToFolderId = "FolderX",
        });

        Assert.Equal("POST", handler.Methods.Single());
        Assert.EndsWith("/me/mailFolders/inbox/messageRules", handler.Urls.Single());

        var body = handler.Bodies.Single()!;
        Assert.Contains("\"displayName\":\"Newsletters\"", body);
        Assert.Contains("senderContains", body);
        Assert.Contains("moveToFolder", body);
        Assert.DoesNotContain("exceptions", body);   // never written, so server-side exceptions survive

        Assert.Equal("new-id", created.Id);
    }

    [Fact]
    public async Task Update_PatchesRuleById()
    {
        var handler = new RecordingHandler(Json("{}"));

        await Service(handler).UpdateAsync(_accountId, new ServerRuleModel
        {
            Id = "r1",
            DisplayName = "Renamed",
            SubjectContains = "invoice",
            MarkAsRead = true,
            IsFullyEditable = true,
        });

        Assert.Equal("PATCH", handler.Methods.Single());
        Assert.EndsWith("/me/mailFolders/inbox/messageRules/r1", handler.Urls.Single());
        var body = handler.Bodies.Single()!;
        Assert.Contains("\"displayName\":\"Renamed\"", body);
        Assert.Contains("subjectContains", body);
        Assert.Contains("markAsRead", body);
    }

    [Fact]
    public async Task SetEnabled_SendsOnlyIsEnabled_SoOtherFieldsSurvive()
    {
        var handler = new RecordingHandler(Json("{}"));

        await Service(handler).SetEnabledAsync(_accountId, "r1", enabled: false);

        Assert.Equal("PATCH", handler.Methods.Single());
        Assert.EndsWith("/messageRules/r1", handler.Urls.Single());
        var body = handler.Bodies.Single()!;
        Assert.Contains("\"isEnabled\":false", body);
        Assert.DoesNotContain("conditions", body);
        Assert.DoesNotContain("actions", body);
    }

    [Fact]
    public async Task Reorder_AssignsSequentialPositions()
    {
        var handler = new RecordingHandler(Json("{}"), Json("{}"), Json("{}"));

        await Service(handler).ReorderAsync(_accountId, ["c", "a", "b"]);

        Assert.Equal(3, handler.Urls.Count);
        Assert.All(handler.Methods, m => Assert.Equal("PATCH", m));
        Assert.EndsWith("/messageRules/c", handler.Urls[0]);
        Assert.Contains("\"sequence\":1", handler.Bodies[0]!);
        Assert.EndsWith("/messageRules/a", handler.Urls[1]);
        Assert.Contains("\"sequence\":2", handler.Bodies[1]!);
        Assert.EndsWith("/messageRules/b", handler.Urls[2]);
        Assert.Contains("\"sequence\":3", handler.Bodies[2]!);
    }

    [Fact]
    public async Task Delete_HitsRuleUrl()
    {
        var handler = new RecordingHandler(Json("{}"));

        await Service(handler).DeleteAsync(_accountId, "r1");

        Assert.Equal("DELETE", handler.Methods.Single());
        Assert.EndsWith("/me/mailFolders/inbox/messageRules/r1", handler.Urls.Single());
    }

    // ── Permission handling ──────────────────────────────────────────────────────

    [Fact]
    public async Task Forbidden_SurfacesConsentRequiredException()
    {
        var handler = new RecordingHandler(Json("""{ "error": { "code": "ErrorAccessDenied" } }""", HttpStatusCode.Forbidden));

        var ex = await Assert.ThrowsAsync<ServerRuleConsentRequiredException>(
            () => Service(handler).ListAsync(_accountId));

        Assert.Contains("administrator", ex.Message);
    }

    [Fact]
    public async Task NonGraphAccount_IsRejected()
    {
        var imap = new AccountModel { Id = _accountId, BackendKind = BackendKind.ImapSmtp, Username = "u@example.com" };
        var svc = new GraphServerRuleService(
            new FixedAccountService(imap),
            new GraphClient(new StubOAuthService(), new HttpClient(new RecordingHandler()), defaultRetryDelay: TimeSpan.Zero));

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ListAsync(_accountId));
    }

    // ── Accessibility: the list row's announced text comes from ToString() ───────

    [Fact]
    public void ToString_CarriesNameStateAndSummary()
    {
        var rule = new ServerRuleModel
        {
            DisplayName = "Newsletters",
            IsEnabled = false,
            SubjectContains = "digest",
            MoveToFolderId = "id",
            MoveToFolderName = "Archive",
        };

        var text = rule.ToString();

        Assert.Contains("Newsletters", text);
        Assert.Contains("disabled", text);
        Assert.Contains("subject contains 'digest'", text);
        Assert.Contains("move to Archive", text);
        Assert.DoesNotContain("ServerRuleModel", text);  // never the type name
    }

    [Fact]
    public void DetailText_ExplainsWhyARuleIsNotEditable()
    {
        var rule = new ServerRuleModel
        {
            DisplayName = "Complex",
            SubjectContains = "x",
            IsFullyEditable = false,
            UnsupportedFields = ["body contains"],
        };

        var text = rule.DetailText();

        Assert.Contains("body contains", text);
        Assert.Contains("Outlook", text);
    }
}
