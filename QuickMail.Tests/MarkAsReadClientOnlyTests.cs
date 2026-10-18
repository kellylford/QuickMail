using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.Services.Graph;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Issue #810, finding 1: Outlook Classic treats "mark it as read" as a client-only action. It cannot show
/// a server rule that has it, and deactivates such rules when it uploads its own. So QuickMail no longer
/// writes markAsRead to a server rule: a new rule that marks read is client-side, and an existing server
/// rule that has it must have it cleared by the user before it can be saved — never dropped unseen.
/// </summary>
public class MarkAsReadClientOnlyTests
{
    private static GraphMessageRule Dto(string actionsJson)
        => new()
        {
            Id = "r1",
            DisplayName = "Digests",
            Sequence = 1,
            IsEnabled = true,
            Conditions = JsonDocument.Parse("""{ "subjectContains": ["digest"] }""").RootElement.Clone(),
            Actions = JsonDocument.Parse(actionsJson).RootElement.Clone(),
        };

    private static string Body(ServerRuleModel rule) => JsonSerializer.Serialize(ServerRuleMapper.ToRequestBody(rule));

    // ── Mapper ───────────────────────────────────────────────────────────────

    [Fact]
    public void ToRequestBody_NeverWritesMarkAsRead()
    {
        var body = Body(new ServerRuleModel { DisplayName = "x", SubjectContains = "x", MarkAsRead = true, MoveToFolderId = "F1" });

        Assert.DoesNotContain("markAsRead", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("moveToFolder", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ToModel_StillReadsMarkAsRead_SoAnExistingRuleShowsAsItIs()
    {
        var model = ServerRuleMapper.ToModel(Dto("""{ "markAsRead": true, "moveToFolder": "F1", "stopProcessingRules": true }"""));

        Assert.True(model.MarkAsRead);
        Assert.True(model.IsFullyEditable);     // editable, so the user can clear it here
        Assert.Contains("mark as read", model.OneLineSummary(), StringComparison.Ordinal);
    }

    [Fact]
    public void DetailText_SaysWhyOutlookCantShowTheRule_AndHowToFixIt()
    {
        var model = ServerRuleMapper.ToModel(Dto("""{ "markAsRead": true }"""));

        Assert.Contains("This rule marks messages as read, so Outlook Classic can't show it. Edit it and clear Mark as read",
                        model.DetailText(), StringComparison.Ordinal);
    }

    [Fact]
    public void DetailText_OfAViewOnlyRule_DoesNotSuggestEditingIt()
    {
        var model = ServerRuleMapper.ToModel(Dto("""{ "markAsRead": true, "assignCategories": ["Red"] }"""));

        Assert.False(model.IsFullyEditable);
        Assert.Contains("This rule marks messages as read, so Outlook Classic can't show it.", model.DetailText(), StringComparison.Ordinal);
        Assert.DoesNotContain("clear Mark as read", model.DetailText(), StringComparison.Ordinal);
    }

    [Fact]
    public void DetailText_OfARuleWithoutMarkAsRead_SaysNothingAboutIt()
        => Assert.DoesNotContain("Outlook Classic", ServerRuleMapper.ToModel(Dto("""{ "moveToFolder": "F1" }""")).DetailText(),
                                 StringComparison.Ordinal);

    // ── Service backstop ─────────────────────────────────────────────────────

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "id": "new" }""", Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class OneAccount(AccountModel account) : IAccountService
    {
        public List<AccountModel> LoadAccounts() => [account];
        public void SaveAccounts(List<AccountModel> accounts) { }
        public void SetDefaultAccount(Guid accountId) { }
    }

    private static (GraphServerRuleService Service, RecordingHandler Handler, Guid AccountId) Service()
    {
        var account = new AccountModel { Id = Guid.NewGuid(), BackendKind = BackendKind.MicrosoftGraph, Username = "user@contoso.com" };
        var handler = new RecordingHandler();
        var service = new GraphServerRuleService(new OneAccount(account),
            new GraphClient(new StubOAuthService(), new HttpClient(handler), defaultRetryDelay: TimeSpan.Zero));
        return (service, handler, account.Id);
    }

    [Fact]
    public async Task Create_RefusesARuleThatMarksRead_AndSendsNothing()
    {
        var (service, handler, id) = Service();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(id,
            new ServerRuleModel { DisplayName = "x", SubjectContains = "x", MarkAsRead = true }, TestContext.Current.CancellationToken));

        Assert.Contains("Mark as read only works in a client-side rule", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task Update_RefusesARuleThatMarksRead_AndSendsNothing()
    {
        var (service, handler, id) = Service();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(id,
            new ServerRuleModel { Id = "r1", DisplayName = "x", SubjectContains = "x", MarkAsRead = true }, TestContext.Current.CancellationToken));

        Assert.Equal(0, handler.Requests);
    }

    // ── Editor ───────────────────────────────────────────────────────────────

    private static ServerRuleEditorViewModel NewRule()
    {
        var vm = ServerRuleEditorViewModel.ForNew();
        vm.Name = "Digests";
        vm.UseSubjectContains = true;
        vm.SubjectContains = "digest";
        return vm;
    }

    [Fact]
    public void ANewRuleThatMarksRead_IsClientSide_OnAMicrosoft365Account()
    {
        var vm = NewRule();
        vm.MarkAsRead = true;
        vm.MoveToFolder = true;
        vm.MoveToFolderId = "folder-id";

        Assert.True(vm.Validate());
        Assert.False(vm.IsServerRepresentable);
        Assert.Equal(RuleRunsWhere.Client, vm.Classify(accountSupportsServerRules: true).Kind);
    }

    [Fact]
    public void ANewRuleThatMarksRead_AndStopsProcessing_SaysWhichTwoClash()
    {
        // The exact shape of the rules in the report, which a client-side rule can't hold either.
        var vm = NewRule();
        vm.MarkAsRead = true;
        vm.StopProcessingRules = true;

        var result = vm.Classify(accountSupportsServerRules: true);

        Assert.Equal("Mark as read only works in a client-side rule, but Stop processing more rules only works in a server-side rule. Remove one to save.",
                     result.ConflictError);
    }

    [Fact]
    public void MarkAsRead_IsOfferedOnANewRule_AndOnAClientRule()
    {
        Assert.True(ServerRuleEditorViewModel.ForNew().CanMarkAsRead);
        Assert.True(ServerRuleEditorViewModel.ForEditClient(new MailRule { Name = "c", SubjectContains = "x", Action = RuleAction.MarkAsRead }).CanMarkAsRead);
    }

    [Fact]
    public void MarkAsRead_IsTurnedOff_WhileEditingAServerRuleThatDoesNotHaveIt()
    {
        var vm = ServerRuleEditorViewModel.ForEdit(new ServerRuleModel { Id = "r1", DisplayName = "x", SubjectContains = "x", MoveToFolderId = "F1" });

        Assert.False(vm.CanMarkAsRead);
        Assert.False(vm.HasServerMarkAsReadNotice);
        Assert.Equal(string.Empty, vm.ServerMarkAsReadNotice);
    }

    [Fact]
    public void AServerRuleThatMarksRead_OpensWithItAvailableToClear_AndSaysWhy()
    {
        var vm = ServerRuleEditorViewModel.ForEdit(ServerRuleMapper.ToModel(Dto("""{ "markAsRead": true, "moveToFolder": "F1" }""")));

        Assert.True(vm.MarkAsRead);           // shown as it is, not quietly unticked
        Assert.True(vm.CanMarkAsRead);
        Assert.True(vm.HasServerMarkAsReadNotice);
        Assert.Equal("This rule runs on the server and marks messages as read. Outlook Classic can't show a server-side rule "
                     + "that marks messages as read, so QuickMail no longer saves one. To save changes to this rule, clear Mark as read.",
                     vm.ServerMarkAsReadNotice);
        Assert.Equal("Mark as read only works in a client-side rule, and this rule runs on the server. Remove it to save.",
                     vm.ServerEditError);
    }

    [Fact]
    public void ClearingMarkAsRead_RemovesTheNotice_AndTheRefusal_ButLeavesItAvailable()
    {
        var vm = ServerRuleEditorViewModel.ForEdit(ServerRuleMapper.ToModel(Dto("""{ "markAsRead": true, "moveToFolder": "F1" }""")));
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.MarkAsRead = false;

        Assert.Contains(nameof(ServerRuleEditorViewModel.HasServerMarkAsReadNotice), changed);
        Assert.Contains(nameof(ServerRuleEditorViewModel.ServerMarkAsReadNotice), changed);
        Assert.False(vm.HasServerMarkAsReadNotice);
        Assert.Null(vm.ServerEditError);
        Assert.True(vm.CanMarkAsRead);        // the user can change their mind before saving

        var body = Body(vm.ToModel());
        Assert.DoesNotContain("markAsRead", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("moveToFolder", body, StringComparison.Ordinal);
    }
}
