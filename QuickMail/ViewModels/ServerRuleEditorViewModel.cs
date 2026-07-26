using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;

namespace QuickMail.ViewModels;

/// <summary>
/// Create/edit form for a single server rule, limited to the editable common subset
/// (<c>docs/planning/server-rules-pm-dev-spec.md</c> §6.3). Rules outside that subset never reach
/// this editor — the list VM blocks Edit — because Graph PATCH replaces conditions/actions wholesale
/// and would drop what we don't model (§16).
/// </summary>
public partial class ServerRuleEditorViewModel : ObservableObject
{
    /// <summary>Identity carried through an edit so the save targets the right rule.</summary>
    private string _ruleId = string.Empty;
    private int _sequence;
    private JsonElement? _rawConditions;
    private JsonElement? _rawActions;
    private JsonElement? _rawExceptions;

    public bool IsNew { get; private init; }

    public string Title => IsNew ? "New server rule" : "Edit server rule";

    // ── Events (View subscribes) ────────────────────────────────────────────

    /// <summary>Ask the View to open the folder picker; returns the chosen folder id (or null).</summary>
    public event Func<(string Id, string Name)?>? PickFolderRequested;

    /// <summary>
    /// Raised on Save with the assembled rule; the owner persists it and returns an error message on
    /// failure (null on success). The editor stays open and shows the error when non-null, so a
    /// rejected save never silently loses the form.
    /// </summary>
    public event Func<ServerRuleModel, Task<string?>>? Saved;

    /// <summary>Raised when the editor window should close (Save or Cancel).</summary>
    public event Action? CloseRequested;

    public event Action<string, AnnouncementCategory>? AnnouncementRequested;

    // ── Factories ───────────────────────────────────────────────────────────

    public static ServerRuleEditorViewModel ForNew() => new() { IsNew = true, Name = string.Empty };

    public static ServerRuleEditorViewModel ForEdit(ServerRuleModel rule)
    {
        var vm = new ServerRuleEditorViewModel
        {
            IsNew = false,
            _ruleId = rule.Id,
            _sequence = rule.Sequence,
            _rawConditions = rule.RawConditions,
            _rawActions = rule.RawActions,
            _rawExceptions = rule.RawExceptions,
            Name = rule.DisplayName,
            IsEnabled = rule.IsEnabled,

            SenderContains = rule.SenderContains ?? string.Empty,
            FromAddresses = string.Join(", ", rule.FromAddresses),
            SentToAddresses = string.Join(", ", rule.SentToAddresses),
            SubjectContains = rule.SubjectContains ?? string.Empty,
            BodyOrSubjectContains = rule.BodyOrSubjectContains ?? string.Empty,
            BodyContains = rule.BodyContains ?? string.Empty,
            SentToMe = rule.SentToMe,
            SentOnlyToMe = rule.SentOnlyToMe,
            HasAttachments = rule.HasAttachments,

            MoveToFolder = !string.IsNullOrWhiteSpace(rule.MoveToFolderId),
            MoveToFolderId = rule.MoveToFolderId,
            MoveToFolderName = rule.MoveToFolderName,
            CopyToFolder = !string.IsNullOrWhiteSpace(rule.CopyToFolderId),
            CopyToFolderId = rule.CopyToFolderId,
            CopyToFolderName = rule.CopyToFolderName,
            MarkAsRead = rule.MarkAsRead,
            Delete = rule.Delete,
            ForwardTo = string.Join(", ", rule.ForwardTo),
            StopProcessingRules = rule.StopProcessingRules,
        };

        vm.SelectedImportance = ImportanceOptions.FirstOrDefault(o =>
            string.Equals(o.Value, rule.Importance, StringComparison.OrdinalIgnoreCase)) ?? ImportanceOptions[0];
        vm.SelectedMarkImportance = ImportanceOptions.FirstOrDefault(o =>
            string.Equals(o.Value, rule.MarkImportance, StringComparison.OrdinalIgnoreCase)) ?? ImportanceOptions[0];
        // If the rule already uses any advanced field, open the Advanced section so editing never
        // hides a populated field. A brand-new rule leaves it collapsed.
        vm.IsAdvancedExpanded = vm.HasAdvancedContent();
        return vm;
    }

    // ── Fields ──────────────────────────────────────────────────────────────

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private bool _isEnabled = true;

    /// <summary>
    /// Whether the Advanced conditions/actions section is expanded. Collapsed for a new rule; opened
    /// automatically when editing a rule that already uses an advanced field (see <see cref="ForEdit"/>).
    /// </summary>
    [ObservableProperty] private bool _isAdvancedExpanded;

    // Conditions
    [ObservableProperty] private string _senderContains = string.Empty;
    [ObservableProperty] private string _fromAddresses = string.Empty;
    [ObservableProperty] private string _sentToAddresses = string.Empty;
    [ObservableProperty] private string _subjectContains = string.Empty;
    [ObservableProperty] private string _bodyOrSubjectContains = string.Empty;
    [ObservableProperty] private string _bodyContains = string.Empty;
    [ObservableProperty] private bool _sentToMe;
    [ObservableProperty] private bool _sentOnlyToMe;
    [ObservableProperty] private bool _hasAttachments;
    [ObservableProperty] private ImportanceOption _selectedImportance = ImportanceOptions[0];

    // Actions
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMoveToFolderSelected))]
    private bool _moveToFolder;
    [ObservableProperty] private string? _moveToFolderId;
    [ObservableProperty] private string? _moveToFolderName;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCopyToFolderSelected))]
    private bool _copyToFolder;
    [ObservableProperty] private string? _copyToFolderId;
    [ObservableProperty] private string? _copyToFolderName;
    [ObservableProperty] private bool _markAsRead;
    /// <summary>Client-only action — Microsoft 365 server rules have no "mark as unread" (spec §20.2).</summary>
    [ObservableProperty] private bool _markAsUnread;
    [ObservableProperty] private ImportanceOption _selectedMarkImportance = ImportanceOptions[0];
    [ObservableProperty] private bool _delete;
    [ObservableProperty] private string _forwardTo = string.Empty;
    [ObservableProperty] private bool _stopProcessingRules;

    public bool IsMoveToFolderSelected => MoveToFolder;
    public bool IsCopyToFolderSelected => CopyToFolder;

    // Validation surfaces
    [ObservableProperty] private string _nameError = string.Empty;
    [ObservableProperty] private string _folderError = string.Empty;
    [ObservableProperty] private string _actionsError = string.Empty;
    /// <summary>A server-side save failure (e.g. Graph rejected the rule), shown on the form.</summary>
    [ObservableProperty] private string _saveError = string.Empty;

    /// <summary>Importance choices for both the condition and the action ComboBoxes.</summary>
    public static List<ImportanceOption> ImportanceOptions { get; } =
    [
        new() { Value = null, DisplayName = "Not set" },
        new() { Value = "low", DisplayName = "Low" },
        new() { Value = "normal", DisplayName = "Normal" },
        new() { Value = "high", DisplayName = "High" },
    ];

    // ── Commands ────────────────────────────────────────────────────────────

    [RelayCommand]
    private void PickFolder()
    {
        if (PickFolderRequested?.Invoke() is not { } picked) return;
        MoveToFolderId = picked.Id;
        MoveToFolderName = picked.Name;
        MoveToFolder = true;
        FolderError = string.Empty;
    }

    [RelayCommand]
    private void PickCopyFolder()
    {
        if (PickFolderRequested?.Invoke() is not { } picked) return;
        CopyToFolderId = picked.Id;
        CopyToFolderName = picked.Name;
        CopyToFolder = true;
        FolderError = string.Empty;
    }

    [RelayCommand]
    private async Task Save()
    {
        if (!Validate()) return;
        SaveError = string.Empty;

        // The owner persists and returns null on success, or an error to display. Close only on
        // success — a failed save keeps the form (and the user's input) and shows why.
        var error = Saved is null ? null : await Saved.Invoke(ToModel());
        if (string.IsNullOrEmpty(error))
        {
            CloseRequested?.Invoke();
            return;
        }

        SaveError = error;
        AnnouncementRequested?.Invoke(error, AnnouncementCategory.Result);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    // ── Assembly & validation ───────────────────────────────────────────────

    public ServerRuleModel ToModel() => new()
    {
        Id = _ruleId,
        Sequence = _sequence,
        DisplayName = Name.Trim(),
        IsEnabled = IsEnabled,

        SenderContains = Blank(SenderContains),
        FromAddresses = SplitAddresses(FromAddresses),
        SentToAddresses = SplitAddresses(SentToAddresses),
        SubjectContains = Blank(SubjectContains),
        BodyOrSubjectContains = Blank(BodyOrSubjectContains),
        BodyContains = Blank(BodyContains),
        SentToMe = SentToMe,
        SentOnlyToMe = SentOnlyToMe,
        HasAttachments = HasAttachments,
        Importance = SelectedImportance?.Value,

        MoveToFolderId = MoveToFolder ? MoveToFolderId : null,
        MoveToFolderName = MoveToFolder ? MoveToFolderName : null,
        CopyToFolderId = CopyToFolder ? CopyToFolderId : null,
        CopyToFolderName = CopyToFolder ? CopyToFolderName : null,
        MarkAsRead = MarkAsRead,
        MarkImportance = SelectedMarkImportance?.Value,
        Delete = Delete,
        ForwardTo = SplitAddresses(ForwardTo),
        StopProcessingRules = StopProcessingRules,

        // Only fully-representable rules ever reach this editor, so the assembled model is safe to
        // PATCH. Raw JSON is carried through unchanged for future merge-based editing.
        IsFullyEditable = true,
        RawConditions = _rawConditions,
        RawActions = _rawActions,
        RawExceptions = _rawExceptions,
    };

    /// <summary>
    /// Assembles a client-side <see cref="MailRule"/> from the client-representable subset of the
    /// form (spec §20.4). Only valid when <see cref="IsClientRepresentable"/> holds — the caller
    /// guarantees a single From/To value and exactly one action, so the mapping is lossless. The
    /// client engine treats a condition as active only when its flag is set AND it has a value, so
    /// empty conditions are simply switched off.
    /// </summary>
    public MailRule ToClientRule(Guid accountId)
    {
        var fromAddrs = SplitAddresses(FromAddresses);
        var from = !string.IsNullOrWhiteSpace(SenderContains) ? SenderContains.Trim()
                 : fromAddrs.Count == 1 ? fromAddrs[0]
                 : null;
        var to = SplitAddresses(SentToAddresses) is { Count: 1 } toList ? toList[0] : null;
        var subject = Blank(SubjectContains);
        var body = Blank(BodyContains);

        return new MailRule
        {
            Name = Name.Trim(),
            IsEnabled = IsEnabled,
            AccountId = accountId,

            UseFromCondition = from is not null, FromContains = from,
            UseToCondition = to is not null, ToContains = to,
            UseSubjectCondition = subject is not null, SubjectContains = subject,
            UseBodyCondition = body is not null, BodyContains = body,
            MustHaveAttachments = HasAttachments,

            Action = ClientAction(),
            TargetFolder = MoveToFolder ? MoveToFolderId : null,
        };
    }

    /// <summary>The single client action in use (IsClientRepresentable guarantees exactly one).</summary>
    private RuleAction ClientAction()
    {
        if (MoveToFolder) return RuleAction.MoveToFolder;
        if (Delete) return RuleAction.Delete;
        if (MarkAsUnread) return RuleAction.MarkAsUnread;
        return RuleAction.MarkAsRead;
    }

    public bool Validate()
    {
        NameError = FolderError = ActionsError = string.Empty;
        var valid = true;

        if (string.IsNullOrWhiteSpace(Name))
        {
            NameError = "Rule name is required.";
            valid = false;
        }

        if (MoveToFolder && string.IsNullOrWhiteSpace(MoveToFolderId))
        {
            FolderError = "Choose a folder for the Move to folder action.";
            valid = false;
        }

        if (CopyToFolder && string.IsNullOrWhiteSpace(CopyToFolderId))
        {
            FolderError = "Choose a folder for the Copy to folder action.";
            valid = false;
        }

        if (!HasAnyAction())
        {
            ActionsError = "Choose at least one action.";
            valid = false;
        }

        if (!valid)
        {
            var errors = new[] { NameError, FolderError, ActionsError }.Where(e => !string.IsNullOrEmpty(e));
            AnnouncementRequested?.Invoke(string.Join(" ", errors), AnnouncementCategory.Result);
        }

        return valid;
    }

    // ── Classification: server vs client (spec §20.3) ───────────────────────

    /// <summary>
    /// Decides where the rule runs. A Graph account gets a server rule unless the rule uses a
    /// client-only capability; otherwise (or on a non-Graph account) it's a client rule, with a
    /// reason for the save dialog. A rule that fits neither — a client-only action combined with a
    /// server-only condition/action — is a conflict the user must resolve. Assumes the rule already
    /// passed <see cref="Validate"/> (so it has at least one action).
    /// </summary>
    public RuleClassification Classify(bool accountSupportsServerRules)
    {
        if (accountSupportsServerRules && IsServerRepresentable)
            return new RuleClassification { Kind = RuleRunsWhere.Server };

        if (IsClientRepresentable)
        {
            var reason = accountSupportsServerRules
                ? $"it uses {Join(ClientOnlyFeaturesUsed())}, which Microsoft 365 server rules don't support"
                : "this account doesn't support server-side rules";
            return new RuleClassification { Kind = RuleRunsWhere.Client, ClientReason = reason };
        }

        // Representable by neither: a client-only action combined with a server-only condition/action,
        // or a server-only feature on a non-Graph account.
        var serverOnly = ServerOnlyFeaturesUsed();
        var clientOnly = ClientOnlyFeaturesUsed();
        var conflict = accountSupportsServerRules && clientOnly.Count > 0
            ? $"{Join(clientOnly)} only works in a QuickMail rule, but {Join(serverOnly)} only works in a server rule. Remove one to save."
            : $"This account only supports QuickMail rules, but {Join(serverOnly)} isn't available in a QuickMail rule. Remove it to save.";
        return new RuleClassification { ConflictError = conflict };
    }

    /// <summary>True when the rule uses no client-only capability, so the server can express it.</summary>
    public bool IsServerRepresentable => ClientOnlyFeaturesUsed().Count == 0;

    /// <summary>
    /// True when every condition and action fits the client rule model (a near-subset of the server
    /// model): no server-only condition/action, single From/To value, exactly one action.
    /// </summary>
    public bool IsClientRepresentable
        => ServerOnlyFeaturesUsed().Count == 0 && ClientEligibleActionCount() == 1;

    /// <summary>Client-only capabilities in use — the server has no equivalent (spec §20.2). Extend
    /// as more client-only options are added (play sound, notify, …).</summary>
    private List<string> ClientOnlyFeaturesUsed()
    {
        var f = new List<string>();
        if (MarkAsUnread) f.Add("Mark as unread");
        return f;
    }

    /// <summary>
    /// Features only a server rule can express, so any of them blocks representing the rule as a
    /// client rule: conditions with no client equivalent, the client's single-value From/To limits,
    /// server-only actions, and the client's one-action limit.
    /// </summary>
    private List<string> ServerOnlyFeaturesUsed()
    {
        var f = new List<string>();

        // Conditions with no client equivalent.
        if (!string.IsNullOrWhiteSpace(BodyOrSubjectContains)) f.Add("the subject-or-body condition");
        if (SentToMe) f.Add("the “sent to me” condition");
        if (SentOnlyToMe) f.Add("the “sent only to me” condition");
        if (SelectedImportance?.Value is not null) f.Add("the importance condition");

        // A client rule has a single From and a single To field.
        var fromAddrs = SplitAddresses(FromAddresses);
        if (fromAddrs.Count > 1) f.Add("multiple From addresses");
        if (!string.IsNullOrWhiteSpace(SenderContains) && fromAddrs.Count > 0)
            f.Add("both Sender-contains and From-addresses");
        if (SplitAddresses(SentToAddresses).Count > 1) f.Add("multiple Sent-to addresses");

        // Actions with no client equivalent.
        if (CopyToFolder) f.Add("Copy to folder");
        if (SelectedMarkImportance?.Value is not null) f.Add("Set importance");
        if (SplitAddresses(ForwardTo).Count > 0) f.Add("Forward");
        if (StopProcessingRules) f.Add("Stop processing more rules");

        // A client rule performs exactly one action.
        if (ClientEligibleActionCount() > 1) f.Add("more than one action");

        return f;
    }

    /// <summary>Count of actions that a client rule could carry (it allows exactly one).</summary>
    private int ClientEligibleActionCount()
    {
        var n = 0;
        if (MarkAsRead) n++;
        if (MarkAsUnread) n++;
        if (MoveToFolder) n++;
        if (Delete) n++;
        return n;
    }

    private static string Join(List<string> items) => string.Join(", ", items);

    /// <summary>
    /// True when any field that lives in the Advanced section is set — used to auto-expand it when
    /// editing. Keep this list in sync with the Advanced group in ServerRuleEditorWindow.xaml.
    /// </summary>
    private bool HasAdvancedContent()
        => !string.IsNullOrWhiteSpace(SenderContains)
           || !string.IsNullOrWhiteSpace(SentToAddresses)
           || !string.IsNullOrWhiteSpace(BodyOrSubjectContains)
           || !string.IsNullOrWhiteSpace(BodyContains)
           || SentToMe || SentOnlyToMe || HasAttachments
           || !string.IsNullOrWhiteSpace(SelectedImportance?.Value)
           || CopyToFolder
           || !string.IsNullOrWhiteSpace(SelectedMarkImportance?.Value)
           || !string.IsNullOrWhiteSpace(ForwardTo);

    private bool HasAnyAction()
        => (MoveToFolder && !string.IsNullOrWhiteSpace(MoveToFolderId))
           || (CopyToFolder && !string.IsNullOrWhiteSpace(CopyToFolderId))
           || MarkAsRead
           || MarkAsUnread
           || Delete
           || StopProcessingRules
           || !string.IsNullOrWhiteSpace(SelectedMarkImportance?.Value)
           || SplitAddresses(ForwardTo).Count > 0;

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Parses a free-text recipient field ("a@b.com, c@d.com; e@f.com").</summary>
    private static List<string> SplitAddresses(string text)
        => string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Where(s => s.Length > 0)
                  .ToList();
}

/// <summary>Where a rule executes: on the Microsoft 365 server, or inside QuickMail.</summary>
public enum RuleRunsWhere { Server, Client }

/// <summary>
/// Result of classifying a rule (spec §20.3). Exactly one of these holds: <see cref="Kind"/> is
/// Server; <see cref="Kind"/> is Client with a <see cref="ClientReason"/> for the save dialog; or
/// <see cref="ConflictError"/> is set (the rule fits neither and must be changed before saving).
/// </summary>
public sealed record RuleClassification
{
    public RuleRunsWhere? Kind { get; init; }
    public string? ClientReason { get; init; }
    public string? ConflictError { get; init; }
    public bool IsConflict => ConflictError is not null;
}

/// <summary>
/// Importance choice for the condition/action ComboBoxes. <c>ToString()</c> is overridden because a
/// screen reader reads a Selector item's accessible name from it, not from DisplayMemberPath.
/// </summary>
public class ImportanceOption
{
    /// <summary>Graph value ("low"/"normal"/"high"), or null for "not set".</summary>
    public string? Value { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public override string ToString() => DisplayName;
}
